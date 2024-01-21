using System;

namespace OrderItemStockLocationAssignment.Models
{
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
}