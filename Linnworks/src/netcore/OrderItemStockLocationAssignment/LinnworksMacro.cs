﻿using System;
using System.Collections.Generic;
using System.Linq;
using LinnworksAPI;

namespace LinnworksMacro
{
    public class LinnworksMacro : LinnworksMacroHelpers.LinnworksMacroBase
    {
        // ReSharper disable once InconsistentNaming
        public void Execute(Guid[] OrderIds, Guid primaryLocationId , Guid alternateLocationId1, Guid alternateLocationId2, Guid alternateLocationId3, Guid alternateLocationId4, Guid alternateLocationId5)
        {
            var alternateLocationIds =
                new[]
                {
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
                if (OrderIds == null || OrderIds.Length == 0)
                {
                    Logger.WriteInfo("No orders provided, skipping macro");
                    return;
                }

                if (alternateLocationIds == null || alternateLocationIds.Length == 0)
                {
                    Logger.WriteInfo("No alternate locations provided, skipping macro");
                    return;
                }

                Logger.WriteInfo($"Order Ids: {string.Join(", ", OrderIds)}");
                Logger.WriteInfo($"Primary Location Id: {primaryLocationId}");
                Logger.WriteInfo($"Alternate Location Ids: {string.Join(", ", alternateLocationIds)}");

                Logger.WriteInfo("Fetching order details");
                var allOrders = Api.Orders.GetOrdersById(OrderIds.ToList());
                var filteredOrders = allOrders.Where(o => o.FulfilmentLocationId == primaryLocationId).ToList();

                Logger.WriteInfo($"Extracting backorder inventory allocations for {filteredOrders.Count} orders.");
                var backorderInventoryAllocations =
                    ExtractBackorderInventoryAllocations(primaryLocationId, alternateLocationIds, filteredOrders);

                Logger.WriteInfo($"Total backorder inventory allocations: {backorderInventoryAllocations.Count}.");
                ProcessBackorderAllocations(backorderInventoryAllocations);
            }
            catch (Exception e)
            {
                Logger.WriteError(e.Message);
                Logger.WriteError(e.StackTrace);
            }
            Logger.WriteInfo("Completed the execution of Linnworks Macro");
        }

        private void ProcessBackorderAllocations(IReadOnlyCollection<BackorderInventoryAllocation> backorderInventoryAllocations)
        {
            foreach (var backorderInventoryAllocation in backorderInventoryAllocations)
            {
                var orderId = backorderInventoryAllocation.OrderId;
                Logger.WriteInfo($"Processing backorder inventory allocation orderId: {orderId}");

                var order = backorderInventoryAllocation.Order;
                var fulfilmentLocationId = backorderInventoryAllocation.FulfilmentLocationId;
                var orderSource = backorderInventoryAllocation.OrderSource;
                var orderSubSource = backorderInventoryAllocation.OrderSubSource;

                // Update order items with the new allocated quantity
                Logger.WriteInfo("Updating order items based on allocations");
                UpdateOrderItemsQuantityBasedOnAllocations(backorderInventoryAllocation, fulfilmentLocationId, orderSource, orderSubSource);

                // Create new order on each location with the allocated quantity
                Logger.WriteInfo("Creating new orders based on allocations");
                foreach (var locationId in backorderInventoryAllocation.LocationIds)
                {
                    CreateAlternateLocationOrder(locationId, order, backorderInventoryAllocation);
                }
            }
        }

        private void CreateAlternateLocationOrder(Guid locationId, OrderDetails order,
            BackorderInventoryAllocation backorderInventoryAllocation)
        {
            Logger.WriteInfo($"Creating new order for location: {locationId}");
            var newOrder = Api.Orders.CreateNewOrder(locationId, false);

            newOrder.GeneralInfo.Status = order.GeneralInfo.Status;
            newOrder.GeneralInfo.Source = order.GeneralInfo.Source;
            newOrder.GeneralInfo.SubSource = order.GeneralInfo.SubSource;

            var updateOrderShippingInfoRequest = new UpdateOrderShippingInfoRequest
            {
                PostalServiceId = order.ShippingInfo.PostalServiceId,
                ManualAdjust = false
            };

            Api.Orders.SetOrderGeneralInfo(newOrder.OrderId, newOrder.GeneralInfo, false);
            Api.Orders.SetOrderShippingInfo(newOrder.OrderId, updateOrderShippingInfoRequest);
            Api.Orders.SetOrderCustomerInfo(newOrder.OrderId, order.CustomerInfo, false);

            var allocatedOrderItems = backorderInventoryAllocation.GetItemAllocationsByLocationId(locationId);
            foreach (var allocatedOrderItem in allocatedOrderItems)
            {
                var itemId = allocatedOrderItem.Key;
                var quantity = allocatedOrderItem.Value;

                var orderItem = backorderInventoryAllocation.GetOrderItem(itemId);
                var linePricingRequest = new LinePricingRequest
                {
                    DiscountPercentage = orderItem.Discount,
                    PricePerUnit = orderItem.PricePerUnit,
                    TaxInclusive = orderItem.TaxCostInclusive,
                    TaxRatePercentage = orderItem.TaxRate
                };

                Api.Orders.AddOrderItem(newOrder.OrderId, itemId, orderItem.ChannelSKU, locationId, quantity,
                    linePricingRequest);
            }

            Logger.WriteInfo($"Finished creating new order for location: {locationId} with Order Id: {newOrder.OrderId}");
            Api.ProcessedOrders.AddOrderNote(order.OrderId, $"Created new order {newOrder.OrderId} to reallocate backorders", true);
            Api.ProcessedOrders.AddOrderNote(newOrder.OrderId, $"This order is reallocated based on order {order.OrderId}", true);
        }

        private void UpdateOrderItemsQuantityBasedOnAllocations(BackorderInventoryAllocation backorderInventoryAllocation,
            Guid fulfilmentLocationId, string orderSource, string orderSubSource)
        {
            Logger.WriteInfo("Starting the update of Order Items quantity based on allocations.");
            foreach (var orderItem in backorderInventoryAllocation.OrderItems)
            {
                var allocatedQuantity = backorderInventoryAllocation.GetAllocatedQuantity(orderItem.StockItemId);
                if (allocatedQuantity == 0)
                {
                    continue;
                }

                Logger.WriteInfo($"Updating quantity for Order Item: {orderItem.StockItemId}.");
                orderItem.Quantity -= allocatedQuantity;

                Api.Orders.UpdateOrderItem(backorderInventoryAllocation.OrderId, orderItem, fulfilmentLocationId,
                    orderSource, orderSubSource);
            }
            Logger.WriteInfo("Updated the order items quantity based on allocations.");
        }

        private IReadOnlyCollection<BackorderInventoryAllocation> ExtractBackorderInventoryAllocations(Guid primaryLocationId, Guid[] alternateLocationIds, List<OrderDetails> orders)
        {
            var backorderInventoryAllocations = new List<BackorderInventoryAllocation>();
            Logger.WriteInfo("Generating backorder availability details for items.");
            var backorderAvailabilityDetailsByItemId = GetBBackorderAvailabilityDetailsByItemId(orders, primaryLocationId, alternateLocationIds);
            foreach (var order in orders)
            {
                var backorderInventoryAllocation = new BackorderInventoryAllocation(order);
                foreach (var orderItem in order.Items)
                {
                    Logger.WriteInfo($"Allocating backorder items quantities for order item: {orderItem.StockItemId}");
                    var allocatedQuantityByLocationId = AllocateBackorderItemQuantityByLocationId(alternateLocationIds, backorderAvailabilityDetailsByItemId, orderItem);
                    if (allocatedQuantityByLocationId.Count != 0)
                    {
                        backorderInventoryAllocation.AddAllocationByItem(orderItem, allocatedQuantityByLocationId);
                    }
                }

                if (backorderInventoryAllocation.AllocationQuantityByLocationAndItem.Count != 0)
                {
                    backorderInventoryAllocations.Add(backorderInventoryAllocation);
                }
            }
            Logger.WriteInfo($"Completed allocation of backorders. Total allocations:{backorderInventoryAllocations.Count}");
            return backorderInventoryAllocations;
        }

        private static IReadOnlyDictionary<Guid, int> AllocateBackorderItemQuantityByLocationId(Guid[] alternateLocationIds,
            IReadOnlyDictionary<Guid, BackorderAvailabilityDetails> backorderAvailabilityDetailsByItemId, OrderItem orderItem)
        {
            if (!backorderAvailabilityDetailsByItemId.TryGetValue(orderItem.StockItemId, out var backorderAvailabilityDetails))
            {
                return new Dictionary<Guid, int>();
            }

            var allocatedQuantityByLocationId = new Dictionary<Guid, int>();
            var unfulfilledQuantity = backorderAvailabilityDetails.Quantity > orderItem.Quantity ? orderItem.Quantity : backorderAvailabilityDetails.Quantity;

            if (unfulfilledQuantity == 0)
            {
                return new Dictionary<Guid, int>();
            }

            for (var i = 0; i < alternateLocationIds.Length; i++)
            {
                var locationId = alternateLocationIds[i];
                var availableQuantity = backorderAvailabilityDetails.AlternateLocationAvailableQuantity[i];

                if (availableQuantity >= unfulfilledQuantity)
                {
                    allocatedQuantityByLocationId.Add(locationId, unfulfilledQuantity);
                    break;
                }

                allocatedQuantityByLocationId.Add(locationId, availableQuantity);
                unfulfilledQuantity -= availableQuantity;
            }

            return allocatedQuantityByLocationId;
        }

        private IReadOnlyDictionary<Guid, BackorderAvailabilityDetails> GetBBackorderAvailabilityDetailsByItemId(
            IEnumerable<OrderDetails> orders,
            Guid primaryLocationId,
            Guid[] alternateLocationIds)
        {
            Logger.WriteInfo("Generating Backorder Availability details.");
            var orderStockItemIds =  orders.SelectMany(o => o.Items).Select(i => i.StockItemId).Distinct().ToList();
            var request = new GetStockLevel_BatchRequest { StockItemIds = orderStockItemIds };

            var batchResponses = Api.Stock.GetStockLevel_Batch(request);

            var result = new Dictionary<Guid, BackorderAvailabilityDetails>();
            var alternateLocationAvailableQuantity = new int[alternateLocationIds.Length];
            foreach (var batchResponse in batchResponses)
            {
                var availableStockInLocations =
                    batchResponse.StockItemLevels.ToDictionary(sil => sil.Location.StockLocationId,
                        sil => sil.Available);

                if (!availableStockInLocations.TryGetValue(primaryLocationId, out var primaryLocationAvailableStock)
                    || primaryLocationAvailableStock > 0)
                {
                    continue;
                }

                for (var i = 0; i < alternateLocationIds.Length; i++)
                {
                    availableStockInLocations.TryGetValue(alternateLocationIds[i],
                        out var alternateLocationAvailableStock);
                    alternateLocationAvailableQuantity[i] = alternateLocationAvailableStock;
                }

                var backorder = new BackorderAvailabilityDetails(primaryLocationAvailableStock, alternateLocationAvailableQuantity);
                result.Add(batchResponse.pkStockItemId, backorder);
            }

            Logger.WriteInfo($"Completed generation of Backorder Availability details for {result.Count} items.");
            return result;
        }
    }

    public class BackorderAvailabilityDetails
    {
        public BackorderAvailabilityDetails(int backorderQuantity, int[] alternateLocationQuantity)
        {
            if (backorderQuantity >= 0)
            {
                throw new ArgumentException("Backorder quantity must be negative");
            }

            Quantity = Math.Abs(backorderQuantity);
            AlternateLocationAvailableQuantity = alternateLocationQuantity;
        }


        public int Quantity { get; }
        public int[] AlternateLocationAvailableQuantity { get; }
    }

    public class BackorderInventoryAllocation
    {
        private readonly OrderDetails _order;
        private readonly Dictionary<ItemLocationKey, int> _allocationQuantityByLocationAndItem;
        private readonly Dictionary<Guid, OrderItem> _orderItemsById = new Dictionary<Guid, OrderItem>();

        public BackorderInventoryAllocation(OrderDetails order)
        {
            OrderId = order.OrderId;
            _order = order;
            _allocationQuantityByLocationAndItem = new Dictionary<ItemLocationKey, int>();
        }

        public void AddAllocationByItem(OrderItem orderItem, IReadOnlyDictionary<Guid, int> quantityByLocationId)
        {
            if (!_orderItemsById.ContainsKey(orderItem.StockItemId))
            {
                _orderItemsById.Add(orderItem.StockItemId, orderItem);
            }

            foreach (var allocation in quantityByLocationId)
            {
                AddAllocation(orderItem.StockItemId, allocation.Key, allocation.Value);
            }
        }

        public int GetAllocatedQuantity(Guid itemId)
        {
            return AllocationQuantityByLocationAndItem
                .Where(a => a.Key.ItemId == itemId)
                .Sum(a => a.Value);
        }

        public Dictionary<Guid, int> GetItemAllocationsByLocationId(Guid locationId)
        {
            return AllocationQuantityByLocationAndItem
                .Where(a => a.Key.LocationId == locationId)
                .ToDictionary(a => a.Key.ItemId, a => a.Value);
        }

        public OrderItem GetOrderItem(Guid itemId)
        {
            return _orderItemsById[itemId];
        }

        public Guid OrderId { get; }
        public OrderDetails Order => _order;
        public string OrderSource => _order.GeneralInfo.Source;
        public string OrderSubSource => _order.GeneralInfo.SubSource;
        public Guid FulfilmentLocationId => _order.FulfilmentLocationId;

        public IReadOnlyDictionary<ItemLocationKey, int> AllocationQuantityByLocationAndItem =>
            _allocationQuantityByLocationAndItem;

        public IReadOnlyCollection<OrderItem> OrderItems => _orderItemsById.Values;

        public IReadOnlyCollection<Guid> LocationIds => AllocationQuantityByLocationAndItem
            .Select(a => a.Key.LocationId)
            .Distinct()
            .ToList();

        private void AddAllocation(Guid itemId, Guid locationId, int quantity)
        {
            var key = new ItemLocationKey {ItemId = itemId, LocationId = locationId};
            if (_allocationQuantityByLocationAndItem.ContainsKey(key))
            {
                _allocationQuantityByLocationAndItem[key] += quantity;
            }
            else
            {
                _allocationQuantityByLocationAndItem.Add(key, quantity);
            }
        }
    }

    public struct ItemLocationKey
    {
        public Guid ItemId { get; set; }
        public Guid LocationId { get; set; }
    }
}
