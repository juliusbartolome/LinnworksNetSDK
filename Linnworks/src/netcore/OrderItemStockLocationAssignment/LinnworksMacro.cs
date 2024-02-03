using System;
using System.Collections.Generic;
using System.Linq;
using LinnworksAPI;

namespace OrderItemStockLocationAssignment
{
    public class LinnworksMacro : LinnworksMacroHelpers.LinnworksMacroBase
    {
        // ReSharper disable once InconsistentNaming
        public void Execute(Guid[] OrderIds, Guid primaryLocationId , Guid alternateLocationId1, Guid alternateLocationId2, Guid alternateLocationId3, Guid alternateLocationId4, Guid alternateLocationId5)
        {
            var alternateLocationIds =
                new[]
                    {
                        primaryLocationId,
                        alternateLocationId1,
                        alternateLocationId2,
                        alternateLocationId3,
                        alternateLocationId4,
                        alternateLocationId5
                    }
                    .Where(id => id != Guid.Empty).ToArray();
            
            Logger.WriteInfo("Starting the execution of Linnworks Macro");
            try
            {
                Logger.WriteInfo("Validating input parameters");
                if (OrderIds == null || OrderIds?.Length == 0)
                {
                    Logger.WriteInfo("No orders provided, skipping macro");
                    return;
                }
                
                if (alternateLocationIds?.Length == 0)
                {
                    Logger.WriteInfo("No alternate locations provided, skipping macro");
                    return;
                }
                
                Logger.WriteInfo($"Order Ids: {string.Join(", ", OrderIds)}");
                Logger.WriteInfo($"Alternate Location Ids: {string.Join(", ", alternateLocationIds)}");

                Logger.WriteInfo("Fetching order details");
                var allOrders = Api.Orders.GetOrdersById(OrderIds.ToList());

                var orders = allOrders.Where(o =>
                    alternateLocationIds.Contains(o.FulfilmentLocationId)).ToList();


                var itemIds = orders.SelectMany(o => o.Items).Select(i => i.StockItemId).Distinct().ToList();
                Logger.WriteInfo($"Getting stock levels for {itemIds.Count} items");
                var itemStockLevelsDictionaryByItemId = GetItemStockLevelDictionary(itemIds, alternateLocationIds);

                foreach (var order in orders)
                {
                    Logger.WriteInfo($"Processing order: {order.NumOrderId} ({order.OrderId}) - Location: {order.FulfilmentLocationId}");
                    foreach (var orderItem in order.Items)
                    {
                        var reallocatedBinRacks = GetReallocatedBinRacks(orderItem, itemStockLevelsDictionaryByItemId,
                            alternateLocationIds);

                        orderItem.BinRacks = reallocatedBinRacks;
                        Logger.WriteInfo($"Updating order item: {orderItem.SKU} ({orderItem.ItemId}) bin racks");
                        Api.Orders.UpdateOrderItem(order.OrderId, orderItem, order.FulfilmentLocationId,
                            order.GeneralInfo.Source, order.GeneralInfo.SubSource);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.WriteError(e.Message);
                Logger.WriteError(e.StackTrace);
            }
            Logger.WriteInfo("Completed the execution of Linnworks Macro");
        }

        private List<OrderItemBinRack> GetReallocatedBinRacks(OrderItem orderItem, IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, ItemStockLevel>> itemStockLevelsDictionaryByItemId,
            Guid[] alternateLocationIds)
        {
            var itemId = orderItem.StockItemId;
            var itemStockLevelsByLocation = itemStockLevelsDictionaryByItemId[itemId]; 
            if (itemStockLevelsByLocation.Count == 0)
            {
                Logger.WriteInfo($"No stock levels found for item: {orderItem.SKU} ({orderItem.ItemId}) in {nameof(itemStockLevelsDictionaryByItemId)}");
                return new List<OrderItemBinRack>();
            }
             
            var binRacksManager = new BinRacksManager(itemStockLevelsByLocation);
            Logger.WriteInfo($"Checking bin racks for order item: {orderItem.SKU} ({orderItem.ItemId})");
            foreach (var binRack in orderItem.BinRacks)
            {
                var runningBinRackQuantity = binRack.Quantity;
                foreach (var alternateLocationId in alternateLocationIds)
                {
                    binRacksManager.AllocateOrder(binRack.Location, alternateLocationId, runningBinRackQuantity);
                    runningBinRackQuantity = binRacksManager.GetQuantity(binRack.Location);
                }
            }

            return binRacksManager.ToList();
        }

        private IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, ItemStockLevel>> GetItemStockLevelDictionary(IReadOnlyCollection<Guid> itemIds, Guid[] alternateLocationIds)
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
                var locationStockLevels = stockLevelItemsByItemIdThenByLocationId.TryGetValue(itemId, out var stockLevelItemsByLocationId)
                    ? stockLevelItemsByLocationId
                    : new Dictionary<Guid, ItemStockLevel>();

                foreach (var alternateLocationId in alternateLocationIds)
                {
                    if (!locationStockLevels.TryGetValue(alternateLocationId, out var stockLevelItem))
                    {
                        locationStockLevels.Add(alternateLocationId, ItemStockLevel.Empty(itemId, alternateLocationId));
                    }
                }
                
                result.Add(itemId, locationStockLevels);
            }

            return result;
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
            OriginalAvailableQuantity = availableQuantity;
            OriginalInOrderQuantity = inOrderQuantity;
            OriginalStockQuantity = availableQuantity + inOrderQuantity;
        }

        public Guid Id { get; }
        public Guid LocationId { get; }
        public int OriginalStockQuantity { get; }
        public int OriginalAvailableQuantity { get; }
        public int OriginalInOrderQuantity { get; }
        public int RunningAvailableQuantity { get; private set; }
        public int RunningInOrderQuantity { get; private set; }
        
        public int GetAllocatedQuantity(int quantity)
        {
            return quantity > RunningAvailableQuantity ? RunningAvailableQuantity : quantity;
        }

        public int GetBackorderQuantity(int quantity)
        {
            return quantity > RunningAvailableQuantity ? quantity - RunningAvailableQuantity : 0;
        }
        
        public void MakeOrder(int quantity)
        {
            if (quantity > RunningAvailableQuantity)
            {
                throw new InvalidOperationException("Insufficient stock available");
            }
            
            RunningAvailableQuantity -= quantity;
            RunningInOrderQuantity += quantity;
        }

        public void PullOutOrder(int quantity)
        {
            if (quantity > RunningInOrderQuantity)
            {
                throw new InvalidOperationException("Insufficient stock in order");
            }
            
            RunningAvailableQuantity += quantity;
            RunningInOrderQuantity -= quantity;
        }
    }

    public class BinRacksManager
    {
        private readonly IReadOnlyDictionary<Guid, ItemStockLevel> stockItemLevelByLocation;
        private readonly Dictionary<Guid, OrderItemBinRack> binRackByLocation = new Dictionary<Guid, OrderItemBinRack>();

        public BinRacksManager(IReadOnlyDictionary<Guid, ItemStockLevel> stockItemLevelByLocation)
        {
            this.stockItemLevelByLocation = stockItemLevelByLocation;
        }
        
        public void AllocateOrder(Guid sourceLocation, Guid targetLocation, int quantity)
        {
            var sourceStockLevel = stockItemLevelByLocation[sourceLocation];
            var targetStockLevel = stockItemLevelByLocation[targetLocation];
            
            if (targetStockLevel.RunningAvailableQuantity == 0)
            {
                return;
            }

            var allocatedQuantity = targetStockLevel.GetAllocatedQuantity(quantity);
            var backorderQuantity = targetStockLevel.GetBackorderQuantity(quantity);
            
            targetStockLevel.MakeOrder(allocatedQuantity);
            sourceStockLevel.PullOutOrder(allocatedQuantity);
            
            SetBinRack(targetLocation, allocatedQuantity);
            SetBinRack(sourceLocation, backorderQuantity);
        }
        
        public int GetQuantity(Guid location) => binRackByLocation.TryGetValue(location, out var binRack) ? binRack.Quantity : 0;
        
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