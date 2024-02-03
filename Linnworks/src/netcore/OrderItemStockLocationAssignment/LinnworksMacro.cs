using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LinnworksAPI;

namespace OrderItemStockLocationAssignment
{

    public class LinnworksMacro : LinnworksMacroHelpers.LinnworksMacroBase
    {
        private IReadOnlyDictionary<Guid, string> locations;
        private IReadOnlyList<Guid> alternateLocationIds;

        // ReSharper disable once InconsistentNaming
        public void Execute(Guid[] OrderIds, Guid primaryLocationId, Guid alternateLocationId1,
            Guid alternateLocationId2, Guid alternateLocationId3, Guid alternateLocationId4, Guid alternateLocationId5)
        {
            Logger.WriteInfo("Starting the execution of Linnworks Macro");
            Logger.WriteInfo("Validating input parameters");
            if (OrderIds == null || OrderIds.Length == 0)
            {
                Logger.WriteInfo("No orders provided, skipping macro");
                return;
            }

            try
            {
                Logger.WriteInfo("Getting locations");
                locations = GetAllLocations();
                alternateLocationIds = GetValidAlternateLocationIds(locations.Keys.ToArray(), primaryLocationId,
                    alternateLocationId1, alternateLocationId2, alternateLocationId3, alternateLocationId4,
                    alternateLocationId5);

                if (alternateLocationIds == null || alternateLocationIds.Count == 0)
                {
                    Logger.WriteInfo("No alternate locations provided, skipping macro");
                    return;
                }

                Logger.WriteInfo($"Order Ids: {string.Join(", ", OrderIds)}");
                Logger.WriteInfo($"Alternate Location Ids: {string.Join(", ", alternateLocationIds)}");

                Logger.WriteInfo("Fetching order details");
                var allOrders = Api.Orders.GetOrdersById(OrderIds.ToList()) ?? new List<OrderDetails>();
                if (allOrders.Count == 0)
                {
                    Logger.WriteInfo($"No orders found from orderIds: {string.Join(", ", OrderIds)}, skipping macro");
                    return;
                }

                var orders = allOrders.Where(o =>
                    alternateLocationIds.Contains(o.FulfilmentLocationId)).ToList();
                if (orders.Count == 0)
                {
                    Logger.WriteInfo(
                        $"No orders found from orderIds: {string.Join(", ", OrderIds)} in alternate locations: {string.Join(", ", alternateLocationIds)}, skipping macro");
                }

                var itemIds = orders.SelectMany(o => o.Items).Select(i => i.StockItemId).Distinct().ToList();
                Logger.WriteInfo($"Getting stock levels for {itemIds.Count} items");
                var itemStockLevelsDictionaryByItemId = GetItemStockLevelDictionary(itemIds);
                if (itemStockLevelsDictionaryByItemId.Count == 0)
                {
                    Logger.WriteInfo("No stock levels found, skipping macro");
                    return;
                }

                foreach (var order in orders)
                {
                    ProcessOrder(order, itemStockLevelsDictionaryByItemId);
                }
            }
            catch (Exception e)
            {
                Logger.WriteError(e.Message);
                Logger.WriteError(e.StackTrace);
            }

            Logger.WriteInfo("Completed the execution of Linnworks Macro");
        }

        private void ProcessOrder(OrderDetails order,
            IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, ItemStockLevel>> itemStockLevelsDictionaryByItemId)
        {
            Logger.WriteInfo(
                $"Processing order: {order.NumOrderId} ({order.OrderId}) - Location: {order.FulfilmentLocationId}");

            foreach (var orderItem in order.Items)
            {
                var itemStockLevelsByLocation = itemStockLevelsDictionaryByItemId[orderItem.StockItemId];
                if (itemStockLevelsByLocation.Count == 0)
                {
                    var message = $"No stock levels found for item: {orderItem.SKU} ({orderItem.ItemId})";
                    Logger.WriteInfo(message);
                    Api.ProcessedOrders.AddOrderNote(order.OrderId, message, true);
                    continue;
                }

                orderItem.BinRacks = ReallocateOrderItemBinRacks(itemStockLevelsByLocation, orderItem);

                AddBinRackReportOrderNote(order.OrderId, orderItem);

                Logger.WriteInfo($"Updating order item: {orderItem.SKU} ({orderItem.ItemId}) bin racks");
                Api.Orders.UpdateOrderItem(order.OrderId, orderItem, order.FulfilmentLocationId,
                    order.GeneralInfo.Source, order.GeneralInfo.SubSource);
            }
        }

        private void AddBinRackReportOrderNote(Guid orderId, OrderItem orderItem)
        {
            if (orderItem.BinRacks.Count <= 1)
            {
                var noBinRackMessage = $"No changes on bin racks for order item: {orderItem.SKU} ({orderItem.ItemId})";
                Logger.WriteInfo(noBinRackMessage);
                Api.ProcessedOrders.AddOrderNote(orderId, noBinRackMessage, true);
                return;
            }

            var reportMessageStringBuilder = new StringBuilder();
            reportMessageStringBuilder.AppendLine(
                $"Finalized reallocation made on item: {orderItem.SKU} ({orderItem.ItemId})");
            foreach (var binRack in orderItem.BinRacks)
            {
                reportMessageStringBuilder.AppendLine($"{locations[binRack.Location]}: {binRack.Quantity}");
            }

            var reportMessage = reportMessageStringBuilder.ToString();
            Logger.WriteInfo(reportMessage);
            Api.ProcessedOrders.AddOrderNote(orderId, reportMessage, true);
        }

        private List<OrderItemBinRack> ReallocateOrderItemBinRacks(
            IReadOnlyDictionary<Guid, ItemStockLevel> itemStockLevelsByLocation, OrderItem orderItem)
        {
            var binRacksManager = new BinRacksManager(itemStockLevelsByLocation);
            Logger.WriteInfo($"Checking bin racks for order item: {orderItem.SKU} ({orderItem.ItemId})");
            foreach (var binRack in orderItem.BinRacks)
            {
                var runningBinRackQuantity = binRack.Quantity;
                foreach (var alternateLocationId in alternateLocationIds)
                {
                    runningBinRackQuantity = binRacksManager.AllocateOrder(binRack.Location, alternateLocationId, runningBinRackQuantity);
                    if (runningBinRackQuantity == 0)
                    {
                        break;
                    }
                }
            }

            return binRacksManager.ToList();
        }

        private IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, ItemStockLevel>> GetItemStockLevelDictionary(
            IReadOnlyCollection<Guid> itemIds)
        {
            var request = new GetStockLevel_BatchRequest { StockItemIds = itemIds.ToList() };
            var batchResponses = Api.Stock.GetStockLevel_Batch(request);

            var stockLevelItemsByItemIdThenByLocationId = (from response in batchResponses
                    from stockItemLevel in response.StockItemLevels
                    where alternateLocationIds.Contains(stockItemLevel.Location.StockLocationId)
                    select new ItemStockLevel(stockItemLevel.StockItemId, stockItemLevel.Location.StockLocationId,
                        stockItemLevel.Available, stockItemLevel.InOrders))
                .GroupBy(l => l.Id, l => l)
                .ToDictionary(grp => grp.Key, grp => grp.ToDictionary(l => l.LocationId, l => l));

            var result = new Dictionary<Guid, IReadOnlyDictionary<Guid, ItemStockLevel>>();
            foreach (var itemId in itemIds)
            {
                var locationStockLevels =
                    stockLevelItemsByItemIdThenByLocationId.TryGetValue(itemId, out var stockLevelItemsByLocationId)
                        ? stockLevelItemsByLocationId
                        : new Dictionary<Guid, ItemStockLevel>();

                foreach (var alternateLocationId in alternateLocationIds)
                {
                    if (!locationStockLevels.TryGetValue(alternateLocationId, out _))
                    {
                        locationStockLevels.Add(alternateLocationId, ItemStockLevel.Empty(itemId, alternateLocationId));
                    }
                }

                result.Add(itemId, locationStockLevels);
            }

            return result;
        }

        private IReadOnlyDictionary<Guid, string> GetAllLocations()
        {
            var allLocations = Api.Inventory.GetStockLocations();
            return allLocations.ToDictionary(l => l.StockLocationId, l => l.LocationName);
        }

        private Guid[] GetValidAlternateLocationIds(Guid[] availableLocations, params Guid[] passedLocationIds)
        {
            var validLocationIds = new List<Guid>();
            foreach (var locationId in passedLocationIds)
            {
                if (locationId == Guid.Empty)
                {
                    continue;
                }

                if (!availableLocations.Contains(locationId))
                {
                    Logger.WriteWarning($"Location Id: {locationId} not found in available locations");
                    continue;
                }

                validLocationIds.Add(locationId);
            }

            return validLocationIds.ToArray();
        }
    }

    public class ItemStockLevel
    {
        public static ItemStockLevel Empty(Guid itemId, Guid locationId) =>
            new ItemStockLevel(itemId, locationId, 0, 0);

        public ItemStockLevel(Guid id, Guid locationId, int availableQuantity, int inOrderQuantity)
        {
            Id = id;
            LocationId = locationId;
            RunningAvailableQuantity = availableQuantity;
            RunningInOrderQuantity = inOrderQuantity;
        }

        public Guid Id { get; }
        public Guid LocationId { get; }
        public int RunningAvailableQuantity { get; private set; }
        public int RunningInOrderQuantity { get; private set; }

        public int GetAllowedAllocationQuantity(int quantity)
        {
            if (RunningAvailableQuantity < 0)
            {
                return quantity + RunningAvailableQuantity;
            }
            
            return quantity < RunningAvailableQuantity ? quantity : RunningAvailableQuantity;
        }

        public int GetBackorderQuantity(int quantity)
        {
            if (RunningAvailableQuantity < 0)
            {
                return Math.Abs(RunningAvailableQuantity);
            }
            
            return quantity > RunningAvailableQuantity ? quantity - RunningAvailableQuantity : 0;
        }

        public void MakeOrder(int quantity)
        {
            if (quantity > RunningAvailableQuantity)
            {
                throw new InvalidOperationException("Insufficient stock available to order");
            }

            RunningAvailableQuantity -= quantity;
            RunningInOrderQuantity += quantity;
        }

        public void PullOutOrder(int quantity)
        {
            if (quantity > RunningInOrderQuantity)
            {
                throw new InvalidOperationException("Insufficient stock in order to pull-out");
            }

            RunningAvailableQuantity += quantity;
            RunningInOrderQuantity -= quantity;
        }
    }

    public class BinRacksManager
    {
        private readonly IReadOnlyDictionary<Guid, ItemStockLevel> stockItemLevelByLocation;

        private readonly Dictionary<Guid, OrderItemBinRack>
            binRackByLocation = new Dictionary<Guid, OrderItemBinRack>();

        public BinRacksManager(IReadOnlyDictionary<Guid, ItemStockLevel> stockItemLevelByLocation)
        {
            this.stockItemLevelByLocation = stockItemLevelByLocation;
        }

        public int AllocateOrder(Guid sourceLocation, Guid targetLocation, int quantity)
        {
            var sourceStockLevel = stockItemLevelByLocation[sourceLocation];
            var targetStockLevel = stockItemLevelByLocation[targetLocation];

            var allocatedQuantity = targetStockLevel.GetAllowedAllocationQuantity(quantity);
            var backorderQuantity = targetStockLevel.GetBackorderQuantity(quantity);

            if (sourceLocation != targetLocation)
            {
                targetStockLevel.MakeOrder(allocatedQuantity);
            }

            sourceStockLevel.PullOutOrder(allocatedQuantity);
            SetBinRack(targetLocation, allocatedQuantity);
            return backorderQuantity;
        }

        private void SetBinRack(Guid location, int quantity)
        {
            if (!binRackByLocation.TryGetValue(location, out var binRack))
            {
                binRackByLocation.Add(location,
                    new OrderItemBinRack { Location = location, Quantity = quantity });
                return;
            }

            binRack.Quantity = quantity;
        }

        public List<OrderItemBinRack> ToList() => binRackByLocation.Values.ToList();
    }
}