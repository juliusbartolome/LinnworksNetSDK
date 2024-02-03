using System;
using LinnworksAPI;

namespace LinnMacroCustomer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            //Replace the following with your application details and installation token
            var applicationId = Guid.Parse("f3456aaf-1bf6-40be-ac43-05deac25ac58");
            var secretKey = Guid.Parse("56c2931f-b01c-40a3-a929-7c56ff5bc746");
            var token = Guid.Parse("4eb405fd8458b4d546701d71345bb777");

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
            
            var orderIds = new[]
            {
                new Guid("cf172064-e6c5-4069-807f-9623b87c1710"),
                new Guid("4c4705a4-ef2d-46ea-b72d-14459e0ec94f"),
                new Guid("77cd43ae-f9ca-4612-9687-4a351a5a9062"),
            };
            
            var primaryLocationId = new Guid("fb26a277-0f33-4c58-8375-a6783aa21cdb");
            var alternateLocationIds = new[]
            {
                new Guid("c4b5b631-36c8-4d96-93df-c150d8632c54"),
                Guid.Empty,
                Guid.Empty,
                Guid.Empty,
                Guid.Empty,
            };

            macro.Execute(orderIds, primaryLocationId, alternateLocationIds[0], alternateLocationIds[1],
                alternateLocationIds[2], alternateLocationIds[3], alternateLocationIds[4]);
        }
    }
}
