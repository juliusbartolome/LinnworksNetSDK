using System;
using LinnworksAPI;

namespace LinnMacroCustomer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            //Replace the following with your application details and installation token
            var applicationId = Guid.NewGuid();
            var secretKey = Guid.NewGuid();
            var token = Guid.NewGuid();

            // ExecuteExampleMacro(applicationId, secretKey, token);
            ExecuteOrderItemStockLocationAssignment(applicationId, secretKey, token);
        }

        private static LinnworksAPI.BaseSession Authorize(Guid applicationId, Guid secretKey, Guid token)
        {
            var controller = new LinnworksAPI.AuthController(new LinnworksAPI.ApiContext("https://api.linnworks.net"));

            return controller.AuthorizeByApplication(new LinnworksAPI.AuthorizeByApplicationRequest
            {
                ApplicationId = applicationId,
                ApplicationSecret = secretKey,
                Token = token
            });
        }

        private static TMacro SetupMacro<TMacro>(Guid applicationId, Guid secretKey, Guid token)
            where TMacro : LinnworksMacroHelpers.LinnworksMacroBase, new()
        {
            var auth = Authorize(applicationId, secretKey, token);

            var context = new LinnworksAPI.ApiContext(auth.Token, auth.Server);

            var url = new Api2Helper().GetUrl(context.ApiServer);

            var macro = new TMacro()
            {
                Api = new LinnworksAPI.ApiObjectManager(context),
                Api2 = new LinnworksAPI2.LinnworksApi2(auth.Token, url),
                Logger = new LoggerProxy(),
            };

            return macro;
        }

        private static void ExecuteExampleMacro(Guid applicationId, Guid secretKey, Guid token)
        {
            var macro = SetupMacro<LinnworksMacro.LinnworksMacro>(applicationId, secretKey, token);
            
            var pkStockItemId = new Guid("37d8fb79-4eea-401b-911a-d5cb04db61a4");
            var result = macro.Execute(pkStockItemId);

            Console.WriteLine(result == null ? "Stock item not found" : result.ItemNumber);     
        }
        
        private static void ExecuteOrderItemStockLocationAssignment(Guid applicationId, Guid secretKey, Guid token)
        {
            var macro = SetupMacro<OrderItemStockLocationAssignment.LinnworksMacro>(applicationId, secretKey, token);
            
            var orderIds = new[] { new Guid("73e79b5b-5070-4ac6-95cc-849d296cc325") };
            var primaryLocationId = new Guid("fb26a277-0f33-4c58-8375-a6783aa21cdb");
            var secondaryLocationId = new Guid("c4b5b631-36c8-4d96-93df-c150d8632c54");
            
            macro.Execute(orderIds, primaryLocationId, secondaryLocationId);
        }
    }
}
