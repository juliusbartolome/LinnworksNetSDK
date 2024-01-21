using System;
using System.Collections.Generic;
using System.Linq;
using LinnworksAPI;

namespace OrderItemStockLocationAssignment.Models
{
    public class BackorderInventoryAllocation
    {
        private readonly OrderDetails order;
        private readonly Dictionary<ItemLocationKey, int> allocationQuantityByLocationAndItem;
        private readonly Dictionary<Guid, OrderItem> orderItemsById = new Dictionary<Guid, OrderItem>();
        
        public BackorderInventoryAllocation(OrderDetails order)
        {
            OrderId = order.OrderId;
            this.order = order;
            allocationQuantityByLocationAndItem = new Dictionary<ItemLocationKey, int>();
        }

        public void AddAllocationByItem(OrderItem orderItem, IReadOnlyDictionary<Guid, int> quantityByLocationId)
        {
            if (!orderItemsById.ContainsKey(orderItem.StockItemId))
            {
                orderItemsById.Add(orderItem.StockItemId, orderItem);
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
            return orderItemsById[itemId];
        }
        
        public Guid OrderId { get; }
        public OrderDetails Order => order;
        public string OrderSource => order.GeneralInfo.Source;
        public string OrderSubSource => order.GeneralInfo.SubSource;
        public Guid FulfilmentLocationId => order.FulfilmentLocationId;

        public IReadOnlyDictionary<ItemLocationKey, int> AllocationQuantityByLocationAndItem =>
            allocationQuantityByLocationAndItem;
        
        public IReadOnlyCollection<OrderItem> OrderItems => orderItemsById.Values;
        
        public IReadOnlyCollection<Guid> LocationIds => AllocationQuantityByLocationAndItem
            .Select(a => a.Key.LocationId)
            .Distinct()
            .ToList();

        private void AddAllocation(Guid itemId, Guid locationId, int quantity)
        {
            var key = new ItemLocationKey {ItemId = itemId, LocationId = locationId};
            if (allocationQuantityByLocationAndItem.ContainsKey(key))
            {
                allocationQuantityByLocationAndItem[key] += quantity;
            }
            else
            {
                allocationQuantityByLocationAndItem.Add(key, quantity);
            }
        }
    }
}