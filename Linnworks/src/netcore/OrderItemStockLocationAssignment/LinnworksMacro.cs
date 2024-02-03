using System;
using System.Collections.Generic;
using System.Linq;
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
                    var orderBinRacks = GetAllOrderBinRacks(order, itemStockLevelsDictionaryByItemId);
                    var originalPostageCost = order.ShippingInfo.PostageCost;
                    var allOrderItemQuantity = orderBinRacks.Sum(br => br.Quantity);

                    var orderBinRacksGroups = orderBinRacks
                        .GroupBy(br => br.LocationId).Where(grp => grp.Any())
                        .ToDictionary(grp => grp.Key,
                            grp => grp.GroupBy(br => br.ItemId)
                                .ToDictionary(brGrp => brGrp.Key,
                                    brGrp => brGrp.Select(br => br.ToOrderItemBinRack()).ToList()));

                    ProcessReallocatedOrders(order, orderBinRacksGroups, originalPostageCost, allOrderItemQuantity);
                    ProcessOriginalOrder(order, orderBinRacksGroups[order.FulfilmentLocationId], originalPostageCost, allOrderItemQuantity);
                }
            }
            catch (Exception e)
            {
                Logger.WriteError(e.Message);
                Logger.WriteError(e.StackTrace);
            }

            Logger.WriteInfo("Completed the execution of Linnworks Macro");
        }

        private void ProcessReallocatedOrders(OrderDetails order, Dictionary<Guid, Dictionary<Guid, List<OrderItemBinRack>>> orderBinRacksGroups, double originalPostageCost,
            int allOrderItemQuantity)
        {
            var clonedOrderItemsByItemId = order.Items.Select(CloneOrderItem).ToDictionary(i => i.ItemId);
            foreach (var locationId in orderBinRacksGroups.Keys)
            {
                if (order.FulfilmentLocationId == locationId)
                {
                    continue;
                }
                
                var locationBinRacksByItemId = orderBinRacksGroups[locationId];
                var locationOrderItemQuantity = 0.0;
                        
                Logger.WriteInfo($"Creating new order for location: {locationId}");
                var newOrder = Api.Orders.CreateNewOrder(locationId, false);

                newOrder.GeneralInfo.Status = order.GeneralInfo.Status;
                newOrder.GeneralInfo.Source = order.GeneralInfo.Source;
                newOrder.GeneralInfo.SubSource = order.GeneralInfo.SubSource;

                foreach (var itemId in locationBinRacksByItemId.Keys)
                {
                    var orderItem = clonedOrderItemsByItemId[itemId];
                    orderItem.BinRacks = locationBinRacksByItemId[itemId];
                    orderItem.Quantity = locationBinRacksByItemId[itemId].Sum(br => br.Quantity);

                    if (orderItem.Quantity <= 0)
                    {
                        continue;
                    }
                    
                    locationOrderItemQuantity += orderItem.Quantity;
                    var linePricingRequest = new LinePricingRequest
                    {
                        DiscountPercentage = orderItem.Discount,
                        PricePerUnit = orderItem.PricePerUnit,
                        TaxInclusive = orderItem.TaxCostInclusive,
                        TaxRatePercentage = orderItem.TaxRate
                    };

                    Api.Orders.AddOrderItem(newOrder.OrderId, itemId, orderItem.ChannelSKU, locationId,
                        orderItem.Quantity, linePricingRequest);
                }
                        
                var updateOrderShippingInfoRequest = new UpdateOrderShippingInfoRequest
                {
                    PostageCost = originalPostageCost * (locationOrderItemQuantity / allOrderItemQuantity),
                    PostalServiceId = order.ShippingInfo.PostalServiceId,
                    ManualAdjust = false
                };

                Api.Orders.SetOrderShippingInfo(newOrder.OrderId, updateOrderShippingInfoRequest);
                Api.Orders.SetOrderGeneralInfo(newOrder.OrderId, newOrder.GeneralInfo, false);
                Api.Orders.SetOrderCustomerInfo(newOrder.OrderId, order.CustomerInfo, false);

                Logger.WriteInfo($"Finished creating new order for location: {locations[locationId]} with Order: {newOrder.NumOrderId} ({newOrder.OrderId})");
                Api.ProcessedOrders.AddOrderNote(order.OrderId, $"Created new order {newOrder.NumOrderId} ({newOrder.OrderId}) to reallocate backorders to {locations[locationId]}", true);
                Api.ProcessedOrders.AddOrderNote(newOrder.OrderId, $"This order is reallocated based on order {order.NumOrderId} ({order.OrderId}) from {locations[order.FulfilmentLocationId]}", true);
            }
        }

        private void ProcessOriginalOrder(OrderDetails order, IDictionary<Guid, List<OrderItemBinRack>> orderBinRacks, double originalPostageCost,
            int allOrderItemQuantity)
        {
            var originalOrderItemQuantity = 0.0;
            foreach (var orderItem in order.Items)
            {
                orderItem.BinRacks = orderBinRacks[orderItem.ItemId];
                orderItem.Quantity = orderItem.BinRacks.Sum(br => br.Quantity);
                originalOrderItemQuantity += orderItem.Quantity;
                if (orderItem.Quantity > 0)
                {
                    Logger.WriteInfo($"Updating order item: {orderItem.SKU} ({orderItem.ItemId}) bin racks");
                    Api.Orders.UpdateOrderItem(order.OrderId, orderItem, order.FulfilmentLocationId,
                        order.GeneralInfo.Source, order.GeneralInfo.SubSource);
                    continue;
                }
                        
                Logger.WriteInfo($"Order item: {orderItem.SKU} ({orderItem.ItemId}) has no stock, deleting order item");
                Api.Orders.RemoveOrderItem(order.OrderId, orderItem.RowId, order.FulfilmentLocationId);
            }
                    
            var updateOriginalOrderShippingInfoRequest = new UpdateOrderShippingInfoRequest
            {
                PostageCost = originalPostageCost * (originalOrderItemQuantity / allOrderItemQuantity),
                PostalServiceId = order.ShippingInfo.PostalServiceId,
                ManualAdjust = false
            };

            Api.Orders.SetOrderShippingInfo(order.OrderId, updateOriginalOrderShippingInfoRequest);
        }

        private IReadOnlyList<ItemBinRack> GetAllOrderBinRacks(OrderDetails order,
            IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, ItemStockLevel>> itemStockLevelsDictionaryByItemId)
        {
            Logger.WriteInfo(
                $"Processing order: {order.NumOrderId} ({order.OrderId}) - Location: {order.FulfilmentLocationId}");

            var allItemBinRacks = new List<ItemBinRack>();
            foreach (var orderItem in order.Items)
            {
                var itemStockLevelsByLocation = itemStockLevelsDictionaryByItemId[orderItem.StockItemId];
                if (itemStockLevelsByLocation.Count == 0)
                {
                    var message = $"No stock levels found for item: {orderItem.SKU} ({orderItem.ItemId})";
                    Logger.WriteInfo(message);
                    continue;
                }

                var computeDistributedItemBinRacks = ComputeDistributedItemBinRacks(itemStockLevelsByLocation, orderItem);
                Logger.WriteInfo($"Found {computeDistributedItemBinRacks.Count} bin racks for order item: {orderItem.SKU} ({orderItem.ItemId})");
                allItemBinRacks.AddRange(computeDistributedItemBinRacks);
            }

            return allItemBinRacks;
        }
        
        private IReadOnlyList<ItemBinRack> ComputeDistributedItemBinRacks(
            IReadOnlyDictionary<Guid, ItemStockLevel> itemStockLevelsByLocation, OrderItem orderItem)
        {
            var binRacksManager = new BinRacksManager(orderItem.ItemId, itemStockLevelsByLocation);
            Logger.WriteInfo($"Checking bin racks for order item: {orderItem.SKU} ({orderItem.ItemId})");
            for (var binRackIndex = 0; binRackIndex < orderItem.BinRacks.Count; binRackIndex++)
            {
                var binRack = orderItem.BinRacks[binRackIndex];
                var runningBinRackQuantity = binRack.Quantity;
                foreach (var alternateLocationId in alternateLocationIds)
                {
                    runningBinRackQuantity = binRacksManager.AllocateOrder(binRackIndex,
                        binRack.Location, alternateLocationId, runningBinRackQuantity);
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
        
        private OrderItem CloneOrderItem(OrderItem orderItem)
        {
            return new OrderItem
            {
                ItemId = orderItem.ItemId,
                ItemNumber = orderItem.ItemNumber,
                SKU = orderItem.SKU,
                ItemSource = orderItem.ItemSource,
                Title = orderItem.Title,
                Quantity = 0,
                CategoryName = orderItem.CategoryName,
                StockLevelsSpecified = orderItem.StockLevelsSpecified,
                OnOrder = orderItem.OnOrder,
                Level = orderItem.Level,
                AvailableStock = orderItem.AvailableStock,
                PricePerUnit = orderItem.PricePerUnit,
                UnitCost = orderItem.UnitCost,
                DespatchStockUnitCost = orderItem.DespatchStockUnitCost,
                Discount = orderItem.Discount,
                Tax = orderItem.Tax,
                TaxRate = orderItem.TaxRate,
                Cost = orderItem.Cost,
                CostIncTax = orderItem.CostIncTax,
                CompositeSubItems = orderItem.CompositeSubItems,
                IsService = orderItem.IsService,
                SalesTax = orderItem.SalesTax,
                TaxCostInclusive = orderItem.TaxCostInclusive,
                PartShipped = orderItem.PartShipped,
                Weight = orderItem.Weight,
                BarcodeNumber = orderItem.BarcodeNumber,
                Market = orderItem.Market,
                ChannelSKU = orderItem.ChannelSKU,
                ChannelTitle = orderItem.ChannelTitle,
                DiscountValue = orderItem.DiscountValue,
                HasImage = orderItem.HasImage,
                ImageId = orderItem.ImageId,
                AdditionalInfo = orderItem.AdditionalInfo,
                StockLevelIndicator = orderItem.StockLevelIndicator,
                ShippingCost = orderItem.ShippingCost,
                PartShippedQty = orderItem.PartShippedQty,
                BatchNumberScanRequired = orderItem.BatchNumberScanRequired,
                SerialNumberScanRequired = orderItem.SerialNumberScanRequired,
                BinRack = orderItem.BinRack,
                InventoryTrackingType = orderItem.InventoryTrackingType,
                isBatchedStockItem = orderItem.isBatchedStockItem,
                IsWarehouseManaged = orderItem.IsWarehouseManaged,
                IsUnlinked = orderItem.IsUnlinked,
                StockItemIntId = orderItem.StockItemIntId,
                StockItemId = orderItem.StockItemId,
            };
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

    public class ItemBinRack
    {
        public ItemBinRack(Guid itemId, Guid locationId, int quantity)
        {
            ItemId = itemId;
            LocationId = locationId;
            Quantity = quantity;
        }

        public Guid ItemId { get; }
        public Guid LocationId { get; }
        public int Quantity { get; set; }
        
        public OrderItemBinRack ToOrderItemBinRack() => new OrderItemBinRack
        {
            Location = LocationId,
            Quantity = Quantity
        };
    }

    public class BinRacksManager
    {
        private readonly Guid itemId;
        private readonly IReadOnlyDictionary<Guid, ItemStockLevel> stockItemLevelByLocation;

        private readonly Dictionary<string, ItemBinRack>
            binRacksByKey = new Dictionary<string, ItemBinRack>();

        public BinRacksManager(Guid itemId, IReadOnlyDictionary<Guid, ItemStockLevel> stockItemLevelByLocation)
        {
            this.itemId = itemId;
            this.stockItemLevelByLocation = stockItemLevelByLocation;
        }

        public int AllocateOrder(int binRackIndex, Guid sourceLocation, Guid targetLocation, int quantity)
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
            var binRackKey = $"{binRackIndex}:{targetLocation}";
            SetBinRack(binRackKey, targetLocation, allocatedQuantity);
            return backorderQuantity;
        }

        private void SetBinRack(string key, Guid location, int quantity)
        {
            if (!binRacksByKey.TryGetValue(key, out var binRack))
            {
                binRacksByKey.Add(key, new ItemBinRack(itemId, location, quantity));
                return;
            }

            binRack.Quantity = quantity;
        }

        public List<ItemBinRack> ToList() => binRacksByKey.Values.ToList();
    }
}