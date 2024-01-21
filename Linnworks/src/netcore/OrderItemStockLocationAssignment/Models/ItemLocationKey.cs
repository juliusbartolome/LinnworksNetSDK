using System;

namespace OrderItemStockLocationAssignment.Models
{
    public struct ItemLocationKey
    {
        public Guid ItemId { get; set; }
        public Guid LocationId { get; set; }
    }
}