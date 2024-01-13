using System;
using System.Collections.Generic;
using System.Linq;
using LinnworksAPI;

namespace OrderItemStockLocationAssignment
{
    public class LinnworksMacro : LinnworksMacroHelpers.LinnworksMacroBase
    {
        public void Execute(Guid[] orderIds, Guid primaryLocationId, Guid secondaryLocationId)
        {
            var orderId = Guid.Empty;
            Logger.WriteInfo("Order Id: " + orderId);
            var orders = Api.Orders.GetOrdersById(orderIds.ToList());
            var ordersForProcessing = orders.Where(o => o.FulfilmentLocationId == primaryLocationId).ToList();

            var locationStockLevelsDictionary = GetLocationStockLevelsDictionary(ordersForProcessing, new[] { primaryLocationId, secondaryLocationId });
            if (!locationStockLevelsDictionary.TryGetValue(primaryLocationId, out var primaryLocationStockLevels))
            {
                primaryLocationStockLevels = new Dictionary<Guid, int>();
            }

            if (!locationStockLevelsDictionary.TryGetValue(secondaryLocationId, out var secondaryLocationStockLevels))
            {
                secondaryLocationStockLevels = new Dictionary<Guid, int>();
            };
            
            foreach (var order in ordersForProcessing)
            {
                Logger.WriteInfo("Order: " + order.OrderId);
                foreach (var orderItem in order.Items)
                {
                    Logger.WriteInfo(orderItem.SKU + " - " + orderItem.Quantity);
                    
                    primaryLocationStockLevels.TryGetValue(orderItem.StockItemId, out var availableQuantityInPrimaryLocation);
                    if (availableQuantityInPrimaryLocation >= orderItem.Quantity)
                    {
                        primaryLocationStockLevels[orderItem.StockItemId] -= orderItem.Quantity;
                        Logger.WriteInfo("Remaining quantity in primary location: " + primaryLocationStockLevels[orderItem.StockItemId]);
                        continue;
                    }
                    
                    if (availableQuantityInPrimaryLocation < orderItem.Quantity)
                    {
                        var remainingQuantity = orderItem.Quantity - availableQuantityInPrimaryLocation;
                        Logger.WriteInfo("Remaining quantity: " + remainingQuantity);
                        
                        secondaryLocationStockLevels.TryGetValue(orderItem.StockItemId, out var availableQuantityInSecondaryLocation);
                        if (availableQuantityInSecondaryLocation >= remainingQuantity)
                        {
                            secondaryLocationStockLevels[orderItem.StockItemId] -= remainingQuantity;
                            Logger.WriteInfo("Remaining quantity in secondary location: " + secondaryLocationStockLevels[orderItem.StockItemId]);
                            continue;
                        }
                        
                        if (availableQuantityInSecondaryLocation < remainingQuantity)
                        {
                            Logger.WriteInfo("Not enough stock in secondary location");
                        }
                    }
                }
            }
        }

        private IReadOnlyDictionary<Guid, Dictionary<Guid, int>> GetLocationStockLevelsDictionary(IEnumerable<OrderDetails> ordersForProcessing, Guid[] locationIds)
        {
            var orderStockItemIds =  ordersForProcessing.SelectMany(o => o.Items).Select(i => i.StockItemId).Distinct().ToList();
            
            var request = new GetStockLevel_BatchRequest { StockItemIds = orderStockItemIds };
            var batchResponses = Api.Stock.GetStockLevel_Batch(request);

            var locationStockLevelDictionary = batchResponses.SelectMany(r => r.StockItemLevels)
                .Where(sl => sl.Available > 0 && locationIds.Contains(sl.Location.StockLocationId))
                .GroupBy(sl => sl.Location.StockLocationId)
                .ToDictionary(group => group.Key, group => group.ToDictionary(sl => sl.StockItemId, sl => sl.Available));

            return locationStockLevelDictionary;
        }
    }
}