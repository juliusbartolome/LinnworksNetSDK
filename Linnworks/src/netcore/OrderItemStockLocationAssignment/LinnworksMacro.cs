using System;
using System.Collections.Generic;
using System.Linq;
using LinnworksAPI;
using OrderItemStockLocationAssignment.Models;

namespace OrderItemStockLocationAssignment
{
    public class LinnworksMacro : LinnworksMacroHelpers.LinnworksMacroBase
    {
        public void Execute(Guid[] orderIds, Guid primaryLocationId ,Guid[] alternateLocationIds)
        {
            Logger.WriteInfo("Starting the execution of Linnworks Macro");
            try
            {
                Logger.WriteInfo("Validating input parameters");
                if (orderIds == null || orderIds?.Length == 0)
                {
                    Logger.WriteInfo("No orders provided, skipping macro");
                    return;
                }
                
                if (alternateLocationIds == null || alternateLocationIds?.Length == 0)
                {
                    Logger.WriteInfo("No alternate locations provided, skipping macro");
                    return;
                }
                
                Logger.WriteInfo($"Order Ids: {string.Join(", ", orderIds)}");
                Logger.WriteInfo($"Primary Location Id: {primaryLocationId}");
                Logger.WriteInfo($"Alternate Location Ids: {string.Join(", ", alternateLocationIds)}");

                Logger.WriteInfo("Fetching order details");
                var allOrders = Api.Orders.GetOrdersById(orderIds.ToList());
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
                    Api.ProcessedOrders.AddOrderNote(orderId, $"Created new order {newOrder.OrderId} to reallocate backorders", true);
                    Api.ProcessedOrders.AddOrderNote(newOrder.OrderId, $"This order is reallocated based on order {orderId}", true);
                }
            }
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
}