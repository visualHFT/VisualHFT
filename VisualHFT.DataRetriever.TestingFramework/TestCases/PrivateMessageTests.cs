using VisualHFT.Commons.Exceptions;
using VisualHFT.Commons.Helpers;
using VisualHFT.DataRetriever.TestingFramework.Core;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.Model;

namespace VisualHFT.DataRetriever.TestingFramework.TestCases
{


    public class PrivateMessageTests
    {
        private readonly ITestOutputHelper _testOutputHelper;
        public PrivateMessageTests(ITestOutputHelper testOutputHelper)
        {
            _testOutputHelper = testOutputHelper;
        }


        /*
         Test Case 1: Place and Cancel a Limit Buy Order Below Market Price
        */
        [Fact]
        public void Test_PrivateMessage_Scenario1()
        {
            
            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors) //run the same test for each plugin
            {
                HelperPosition.Instance.Reset();
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange & Act -> This will execute the private message scenario, creating the expected executed orders
                List<VisualHFT.Model.Order> _expectedExecutedOrders = mktConnector.ExecutePrivateMessageScenario(eTestingPrivateMessageScenario.SCENARIO_1);
                var _expectedOrderSent = _expectedExecutedOrders
                    .FirstOrDefault(x => x.Status == eORDERSTATUS.CANCELED);

                var _actualPosition = HelperPosition.Instance.GetAllPositions().FirstOrDefault();
                double newMarketPrice = _expectedOrderSent.PricePlaced * 1.25;//current price increased by 25%
                _actualPosition.UpdateCurrentMidPrice(newMarketPrice);
                var _actualOrders = _actualPosition.GetAllOrders(null);
                var _actualOrderSent = _actualOrders.FirstOrDefault();


                //ORDER SENT ASSERTION
                Assert.NotNull(_actualOrders);
                Assert.Single(_actualOrders);       //must be one order (we sent just one order, plus updates received from exchange for that same order)
                Assert.NotNull(_actualOrderSent);
                Assert.NotNull(_expectedOrderSent);

                // _expectedOrderSent   -> What VisualHFT expects as order
                // _actualOrderSent     -> The actual order the plugin has generated.
                Assert.NotEqual(0, _actualOrderSent.OrderID);
                Assert.Equal(_expectedOrderSent.OrderID, _actualOrderSent.OrderID);
                Assert.Equal(_expectedOrderSent.Status, _actualOrderSent.Status);
                Assert.Equal(_expectedOrderSent.ProviderId, _actualOrderSent.ProviderId);
                Assert.Equal(_expectedOrderSent.Symbol, _actualOrderSent.Symbol);
                Assert.Equal(_expectedOrderSent.Side, _actualOrderSent.Side);
                Assert.Equal(_expectedOrderSent.OrderType, _actualOrderSent.OrderType);
                Assert.Equal(_expectedOrderSent.PricePlaced, _actualOrderSent.PricePlaced);
                Assert.Equal(_expectedOrderSent.Quantity, _actualOrderSent.Quantity);
                Assert.Equal(_expectedOrderSent.FilledQuantity, _actualOrderSent.FilledQuantity);
                Assert.Equal(_expectedOrderSent.FilledPrice, _actualOrderSent.FilledPrice);

                //POSITIONS STATUS: Assert the Position Manager has the same order (expected) as the actual order
                Assert.Equal(_expectedOrderSent.Symbol, _actualPosition.Symbol);
                Assert.Equal(0, _actualPosition.WrkBuy);
                Assert.Equal(0, _actualPosition.WrkSell);
                Assert.Equal(0, _actualPosition.Exposure);
                Assert.Equal(0, _actualPosition.NetPosition);
                Assert.Equal(0, _actualPosition.PLOpen);
                Assert.Equal(0, _actualPosition.PLRealized);
                Assert.Equal(0, _actualPosition.PLTot);
                Assert.Equal(0, _actualPosition.TotBuy);
                Assert.Equal(0, _actualPosition.TotSell);
                

                //SPECIFIC ASSERTIONS: last order received must be wit status "Canceled"
                var _actualLastOrderReceived = _actualOrders.Last();
                Assert.NotNull(_actualLastOrderReceived);
                Assert.Equal(_expectedOrderSent.OrderID, _actualLastOrderReceived.OrderID);
                Assert.Equal(eORDERSTATUS.CANCELED, _actualLastOrderReceived.Status);

                _testOutputHelper.WriteLine($"TESTING {CONNECTOR_NAME} OK");
            }
        }
        
        
        /*
         Test Case 2: Place and Cancel a Limit Sell Order Above Market Price
        */
        [Fact]
        public void Test_PrivateMessage_Scenario2()
        {

            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors) //run the same test for each plugin
            {
                HelperPosition.Instance.Reset();
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange & Act -> This will execute the private message scenario, creating the expected executed orders
                List<VisualHFT.Model.Order> _expectedExecutedOrders = mktConnector.ExecutePrivateMessageScenario(eTestingPrivateMessageScenario.SCENARIO_2);
                var _expectedOrderSent = _expectedExecutedOrders
                    .FirstOrDefault(x => x.Status == eORDERSTATUS.CANCELED);

                var _actualPosition = HelperPosition.Instance.GetAllPositions().FirstOrDefault();
                double newMarketPrice = _expectedOrderSent.PricePlaced * 1.25;//current price increased by 25%
                _actualPosition.UpdateCurrentMidPrice(newMarketPrice);
                var _actualOrders = _actualPosition.GetAllOrders(null);
                var _actualOrderSent = _actualOrders.FirstOrDefault();


                //ORDER SENT ASSERTION
                Assert.NotNull(_actualOrders);
                Assert.Single(_actualOrders);       //must be one order (we sent just one order, plus updates received from exchange for that same order)
                Assert.NotNull(_actualOrderSent);
                Assert.NotNull(_expectedOrderSent);

                // _expectedOrderSent   -> What VisualHFT expects as order
                // _actualOrderSent     -> The actual order the plugin has generated.
                Assert.NotEqual(0, _actualOrderSent.OrderID);
                Assert.Equal(_expectedOrderSent.OrderID, _actualOrderSent.OrderID);
                Assert.Equal(_expectedOrderSent.Status, _actualOrderSent.Status);
                Assert.Equal(_expectedOrderSent.ProviderId, _actualOrderSent.ProviderId);
                Assert.Equal(_expectedOrderSent.Symbol, _actualOrderSent.Symbol);
                Assert.Equal(_expectedOrderSent.Side, _actualOrderSent.Side);
                Assert.Equal(_expectedOrderSent.OrderType, _actualOrderSent.OrderType);
                Assert.Equal(_expectedOrderSent.PricePlaced, _actualOrderSent.PricePlaced);
                Assert.Equal(_expectedOrderSent.Quantity, _actualOrderSent.Quantity);
                Assert.Equal(_expectedOrderSent.FilledQuantity, _actualOrderSent.FilledQuantity);
                Assert.Equal(_expectedOrderSent.FilledPrice, _actualOrderSent.FilledPrice);

                //POSITIONS STATUS: Assert the Position Manager has the same order (expected) as the actual order
                Assert.Equal(_expectedOrderSent.Symbol, _actualPosition.Symbol);
                Assert.Equal(0, _actualPosition.WrkBuy);
                Assert.Equal(0, _actualPosition.WrkSell);
                Assert.Equal(0, _actualPosition.Exposure);
                Assert.Equal(0, _actualPosition.NetPosition);
                Assert.Equal(0, _actualPosition.PLOpen);
                Assert.Equal(0, _actualPosition.PLRealized);
                Assert.Equal(0, _actualPosition.PLTot);
                Assert.Equal(0, _actualPosition.TotBuy);
                Assert.Equal(0, _actualPosition.TotSell);


                //SPECIFIC ASSERTIONS: last order received must be wit status "Canceled"
                var _actualLastOrderReceived = _actualOrders.Last();
                Assert.NotNull(_actualLastOrderReceived);
                Assert.Equal(_expectedOrderSent.OrderID, _actualLastOrderReceived.OrderID);
                Assert.Equal(eORDERSTATUS.CANCELED, _actualLastOrderReceived.Status);

                _testOutputHelper.WriteLine($"TESTING {CONNECTOR_NAME} OK");
            }
        }

        /*
         Test Case 3: Place a Market Buy Order
        */
        [Fact]
        public void Test_PrivateMessage_Scenario3()
        {

            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors) //run the same test for each plugin
            {
                HelperPosition.Instance.Reset();
                
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");


                //Arrange & Act -> This will execute the private message scenario, creating the expected executed orders
                List<VisualHFT.Model.Order> _expectedExecutedOrders = mktConnector.ExecutePrivateMessageScenario(eTestingPrivateMessageScenario.SCENARIO_3);
                var _expectedOrderSent = _expectedExecutedOrders
                    .FirstOrDefault(x => x.Status == eORDERSTATUS.FILLED);

                var _actualPosition = HelperPosition.Instance.GetAllPositions().FirstOrDefault();
                double newMarketPrice = _expectedOrderSent.PricePlaced * 1.25;//current price increased by 25%
                _actualPosition.UpdateCurrentMidPrice(newMarketPrice); 
                var _actualOrders = _actualPosition.GetAllOrders(null);
                var _actualOrderSent = _actualOrders.FirstOrDefault();

                //ORDER SENT ASSERTION
                Assert.NotNull(_actualOrders);
                Assert.Single(_actualOrders);       //must be one order (we sent just one order, plus updates received from exchange for that same order)
                Assert.NotNull(_actualOrderSent);
                Assert.NotNull(_expectedOrderSent);

                // _expectedOrderSent   -> What VisualHFT expects as order
                // _actualOrderSent     -> The actual order the plugin has generated.
                Assert.NotEqual(0, _actualOrderSent.OrderID);
                Assert.Equal(_expectedOrderSent.OrderID, _actualOrderSent.OrderID);
                Assert.Equal(_expectedOrderSent.Status, _actualOrderSent.Status);
                Assert.Equal(_expectedOrderSent.ProviderId, _actualOrderSent.ProviderId);
                Assert.Equal(_expectedOrderSent.Symbol, _actualOrderSent.Symbol);
                Assert.Equal(_expectedOrderSent.Side, _actualOrderSent.Side);
                Assert.Equal(_expectedOrderSent.OrderType, _actualOrderSent.OrderType);
                Assert.Equal(_expectedOrderSent.PricePlaced, _actualOrderSent.PricePlaced);
                Assert.Equal(_expectedOrderSent.Quantity, _actualOrderSent.Quantity);
                Assert.Equal(_expectedOrderSent.FilledQuantity, _actualOrderSent.FilledQuantity);
                Assert.Equal(_expectedOrderSent.FilledPrice, _actualOrderSent.FilledPrice);
                Assert.Equal(_expectedOrderSent.PendingQuantity, _actualOrderSent.PendingQuantity);

                //POSITIONS STATUS: Assert the Position Manager has the same order (expected) as the actual order
                Assert.Equal(_expectedOrderSent.Symbol, _actualPosition.Symbol);
                Assert.Equal(0, _actualPosition.WrkBuy);
                Assert.Equal(0, _actualPosition.WrkSell);
                Assert.Equal((_expectedOrderSent.FilledQuantity * newMarketPrice), _actualPosition.Exposure);
                Assert.Equal(_expectedOrderSent.FilledQuantity, _actualPosition.NetPosition);
                Assert.Equal((_expectedOrderSent.FilledQuantity * (newMarketPrice - _expectedOrderSent.PricePlaced)), _actualPosition.PLOpen);
                Assert.Equal(0, _actualPosition.PLRealized);
                Assert.Equal((_expectedOrderSent.FilledQuantity * (newMarketPrice - _expectedOrderSent.PricePlaced)), _actualPosition.PLTot);
                Assert.Equal(_expectedOrderSent.FilledQuantity, _actualPosition.TotBuy);
                Assert.Equal(0, _actualPosition.TotSell);


                //SPECIFIC ASSERTIONS: last order received must be wit status "Canceled"
                var _actualLastOrderReceived = _actualOrders.Last();
                Assert.NotNull(_actualLastOrderReceived);
                Assert.Equal(_expectedOrderSent.OrderID, _actualLastOrderReceived.OrderID);
                Assert.Equal(eORDERSTATUS.FILLED, _actualLastOrderReceived.Status);

                _testOutputHelper.WriteLine($"TESTING {CONNECTOR_NAME} OK");
            }
        }
        /*
         Test Case 4: Place a Market Sell Order
        */
        [Fact]
        public void Test_PrivateMessage_Scenario4()
        {
            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors) //run the same test for each plugin
            {
                HelperPosition.Instance.Reset();

                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange & Act -> This will execute the private message scenario, creating the expected executed orders
                List<VisualHFT.Model.Order> _expectedExecutedOrders = mktConnector.ExecutePrivateMessageScenario(eTestingPrivateMessageScenario.SCENARIO_4);
                var _expectedOrderSent = _expectedExecutedOrders
                    .FirstOrDefault(x => x.Status == eORDERSTATUS.FILLED);

                var _actualPosition = HelperPosition.Instance.GetAllPositions().FirstOrDefault();
                double newMarketPrice = _expectedOrderSent.PricePlaced * 1.25;//current price increased by 25%
                _actualPosition.UpdateCurrentMidPrice(newMarketPrice);
                var _actualOrders = _actualPosition.GetAllOrders(null);
                var _actualOrderSent = _actualOrders.FirstOrDefault();


                //ORDER SENT ASSERTION
                Assert.NotNull(_actualOrders);
                Assert.Single(_actualOrders);       //must be one order (we sent just one order, plus updates received from exchange for that same order)
                Assert.NotNull(_actualOrderSent);
                Assert.NotNull(_expectedOrderSent);

                // _expectedOrderSent   -> What VisualHFT expects as order
                // _actualOrderSent     -> The actual order the plugin has generated.
                Assert.NotEqual(0, _actualOrderSent.OrderID);
                Assert.Equal(_expectedOrderSent.OrderID, _actualOrderSent.OrderID);
                Assert.Equal(_expectedOrderSent.Status, _actualOrderSent.Status);
                Assert.Equal(_expectedOrderSent.ProviderId, _actualOrderSent.ProviderId);
                Assert.Equal(_expectedOrderSent.Symbol, _actualOrderSent.Symbol);
                Assert.Equal(_expectedOrderSent.Side, _actualOrderSent.Side);
                Assert.Equal(_expectedOrderSent.OrderType, _actualOrderSent.OrderType);
                Assert.Equal(_expectedOrderSent.PricePlaced, _actualOrderSent.PricePlaced);
                Assert.Equal(_expectedOrderSent.Quantity, _actualOrderSent.Quantity);
                Assert.Equal(_expectedOrderSent.FilledQuantity, _actualOrderSent.FilledQuantity);
                Assert.Equal(_expectedOrderSent.FilledPrice, _actualOrderSent.FilledPrice);
                Assert.Equal(_expectedOrderSent.PendingQuantity, _actualOrderSent.PendingQuantity);

                //POSITIONS STATUS: Assert the Position Manager has the same order (expected) as the actual order
                Assert.Equal(_expectedOrderSent.Symbol, _actualPosition.Symbol);
                Assert.Equal(0, _actualPosition.WrkBuy);
                Assert.Equal(0, _actualPosition.WrkSell);
                Assert.Equal(-(_expectedOrderSent.FilledQuantity * newMarketPrice), _actualPosition.Exposure);
                Assert.Equal(-_expectedOrderSent.FilledQuantity, _actualPosition.NetPosition);
                Assert.Equal((_expectedOrderSent.FilledQuantity * (_expectedOrderSent.PricePlaced - newMarketPrice)), _actualPosition.PLOpen);
                Assert.Equal(0, _actualPosition.PLRealized);
                Assert.Equal((_expectedOrderSent.FilledQuantity * (_expectedOrderSent.PricePlaced - newMarketPrice)), _actualPosition.PLTot);
                Assert.Equal(0, _actualPosition.TotBuy);
                Assert.Equal(_expectedOrderSent.FilledQuantity, _actualPosition.TotSell);


                //SPECIFIC ASSERTIONS: last order received must be wit status "Canceled"
                var _actualLastOrderReceived = _actualOrders.Last();
                Assert.NotNull(_actualLastOrderReceived);
                Assert.Equal(_expectedOrderSent.OrderID, _actualLastOrderReceived.OrderID);
                Assert.Equal(eORDERSTATUS.FILLED, _actualLastOrderReceived.Status);

                _testOutputHelper.WriteLine($"TESTING {CONNECTOR_NAME} OK");
            }
        }

        /*
         Test Case 5: Place a Limit Buy Order That Gets Partially Filled
         */
        [Fact]
        public void Test_PrivateMessage_Scenario5()
        {
            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors) //run the same test for each plugin
            {
                HelperPosition.Instance.Reset();

                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange & Act -> This will execute the private message scenario, creating the expected executed orders
                List<VisualHFT.Model.Order> _expectedExecutedOrders = mktConnector.ExecutePrivateMessageScenario(eTestingPrivateMessageScenario.SCENARIO_5);
                
                var _expectedOrderSent = _expectedExecutedOrders
                    .FirstOrDefault(x => x.Status == eORDERSTATUS.CANCELED);

                var _actualPosition = HelperPosition.Instance.GetAllPositions().FirstOrDefault();
                double newMarketPrice = _expectedOrderSent.PricePlaced * 1.25;//current price increased by 25%
                _actualPosition.UpdateCurrentMidPrice(newMarketPrice);
                var _actualOrders = _actualPosition.GetAllOrders(null);
                var _actualOrderSent = _actualOrders.FirstOrDefault();


                //ORDER SENT ASSERTION
                Assert.NotNull(_actualOrders);
                Assert.Single(_actualOrders);       //must be one order (we sent just one order, plus updates received from exchange for that same order)
                Assert.NotNull(_actualOrderSent);
                Assert.NotNull(_expectedOrderSent);

                // _expectedOrderSent   -> What VisualHFT expects as order
                // _actualOrderSent     -> The actual order the plugin has generated.
                Assert.NotEqual(0, _actualOrderSent.OrderID);
                Assert.Equal(_expectedOrderSent.OrderID, _actualOrderSent.OrderID);
                Assert.Equal(_expectedOrderSent.Status, _actualOrderSent.Status);
                Assert.Equal(_expectedOrderSent.ProviderId, _actualOrderSent.ProviderId);
                Assert.Equal(_expectedOrderSent.Symbol, _actualOrderSent.Symbol);
                Assert.Equal(_expectedOrderSent.Side, _actualOrderSent.Side);
                Assert.Equal(_expectedOrderSent.OrderType, _actualOrderSent.OrderType);
                Assert.Equal(_expectedOrderSent.PricePlaced, _actualOrderSent.PricePlaced);
                Assert.Equal(_expectedOrderSent.Quantity, _actualOrderSent.Quantity);
                Assert.Equal(_expectedOrderSent.FilledQuantity, _actualOrderSent.FilledQuantity);
                Assert.Equal(_expectedOrderSent.FilledPrice, _actualOrderSent.FilledPrice);
                Assert.Equal(_expectedOrderSent.PendingQuantity, _actualOrderSent.PendingQuantity);

                //POSITIONS STATUS: Assert the Position Manager has the same order (expected) as the actual order
                Assert.Equal(_expectedOrderSent.Symbol, _actualPosition.Symbol);
                Assert.Equal(0, _actualPosition.WrkBuy);
                Assert.Equal(0, _actualPosition.WrkSell);
                Assert.Equal((_expectedOrderSent.FilledQuantity * newMarketPrice), _actualPosition.Exposure);
                Assert.Equal(_expectedOrderSent.FilledQuantity, _actualPosition.NetPosition);
                Assert.Equal((_expectedOrderSent.FilledQuantity * (newMarketPrice - _expectedOrderSent.PricePlaced)), _actualPosition.PLOpen);
                Assert.Equal(0, _actualPosition.PLRealized);
                Assert.Equal((_expectedOrderSent.FilledQuantity * (newMarketPrice - _expectedOrderSent.PricePlaced)), _actualPosition.PLTot);
                Assert.Equal(_expectedOrderSent.FilledQuantity, _actualPosition.TotBuy);
                Assert.Equal(0, _actualPosition.TotSell);


                //SPECIFIC ASSERTIONS: last order received must be wit status "Canceled"
                var _actualLastOrderReceived = _actualOrders.Last();
                Assert.NotNull(_actualLastOrderReceived);
                Assert.Equal(_expectedOrderSent.OrderID, _actualLastOrderReceived.OrderID);
                Assert.Equal(eORDERSTATUS.CANCELED, _actualLastOrderReceived.Status);

                _testOutputHelper.WriteLine($"TESTING {CONNECTOR_NAME} OK");
            }
        }

        /*
        Test Case 6: Modify an Existing Limit Order
        */
        [Fact]
       public void Test_PrivateMessage_Scenario6()
       {
            //1. Order sent
            //2. Order modified
            //3. Order cancelled


            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
           foreach (var mktConnector in marketConnectors) //run the same test for each plugin
           {
               HelperPosition.Instance.Reset();

               var CONNECTOR_NAME = mktConnector.GetType().Name;
               _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange & Act -> This will execute the private message scenario, creating the expected executed orders
                List<VisualHFT.Model.Order> _expectedExecutedOrders = mktConnector.ExecutePrivateMessageScenario(eTestingPrivateMessageScenario.SCENARIO_6);
               var _expectedOrderSent = _expectedExecutedOrders
                   .LastOrDefault(x => x.Status == eORDERSTATUS.CANCELED);

               var _actualPosition = HelperPosition.Instance.GetAllPositions().FirstOrDefault();
               double newMarketPrice = _expectedOrderSent.PricePlaced * 1.25;//current price increased by 25%
               _actualPosition.UpdateCurrentMidPrice(newMarketPrice);
               var _actualOrders = _actualPosition.GetAllOrders(null);
               var _actualOrderSent = _actualOrders.LastOrDefault();


               //ORDER SENT ASSERTION
               Assert.NotNull(_actualOrders);
               Assert.Equal(_actualOrders.Count, _expectedExecutedOrders.Count);
               Assert.NotNull(_actualOrderSent);
               Assert.NotNull(_expectedOrderSent);

               // _expectedOrderSent   -> What VisualHFT expects as order
               // _actualOrderSent     -> The actual order the plugin has generated.
               Assert.NotEqual(0, _actualOrderSent.OrderID);
               Assert.Equal(_expectedOrderSent.OrderID, _actualOrderSent.OrderID);
               Assert.Equal(_expectedOrderSent.Status, _actualOrderSent.Status);
               Assert.Equal(_expectedOrderSent.ProviderId, _actualOrderSent.ProviderId);
               Assert.Equal(_expectedOrderSent.Symbol, _actualOrderSent.Symbol);
               Assert.Equal(_expectedOrderSent.Side, _actualOrderSent.Side);
               Assert.Equal(_expectedOrderSent.OrderType, _actualOrderSent.OrderType);
               Assert.Equal(_expectedOrderSent.PricePlaced, _actualOrderSent.PricePlaced);
               Assert.Equal(_expectedOrderSent.Quantity, _actualOrderSent.Quantity);
               Assert.Equal(_expectedOrderSent.FilledQuantity, _actualOrderSent.FilledQuantity);
               Assert.Equal(_expectedOrderSent.FilledPrice, _actualOrderSent.FilledPrice);
               Assert.Equal(_expectedOrderSent.PendingQuantity, _actualOrderSent.PendingQuantity);

               //POSITIONS STATUS: Assert the Position Manager has the same order (expected) as the actual order
               Assert.Equal(_expectedOrderSent.Symbol, _actualPosition.Symbol);
               Assert.Equal(0, _actualPosition.WrkBuy);
               Assert.Equal(0, _actualPosition.WrkSell);
               Assert.Equal(0, _actualPosition.Exposure);
               Assert.Equal(0, _actualPosition.NetPosition);
               Assert.Equal(0, _actualPosition.PLOpen);
               Assert.Equal(0, _actualPosition.PLRealized);
               Assert.Equal(0, _actualPosition.PLTot);
               Assert.Equal(0, _actualPosition.TotBuy);
               Assert.Equal(0, _actualPosition.TotSell);


               //SPECIFIC ASSERTIONS: last order received must be wit status "Canceled"
               var _actualLastOrderReceived = _actualOrders.Last();
               Assert.NotNull(_actualLastOrderReceived);
               Assert.Equal(_expectedOrderSent.OrderID, _actualLastOrderReceived.OrderID);
               Assert.Equal(eORDERSTATUS.CANCELED, _actualLastOrderReceived.Status);

               _testOutputHelper.WriteLine($"TESTING {CONNECTOR_NAME} OK");
            }
        }


        /*
        Test Case 7: Place an Order with Invalid Parameters
        */
        [Fact]
        public void Test_PrivateMessage_Scenario7()
        {
            //1. Placed order with invalid parameters
            //2. Exchange should reject
            //3. Order cancelled
            return;
            HelperPosition.Instance.Reset();


            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors) //run the same test for each plugin
            {
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange & Act -> This will execute the private message scenario, creating the expected executed orders
                List<VisualHFT.Model.Order> _expectedExecutedOrders = mktConnector.ExecutePrivateMessageScenario(eTestingPrivateMessageScenario.SCENARIO_7);
                var _expectedOrderSent = _expectedExecutedOrders
                    .FirstOrDefault(x => x.Status == eORDERSTATUS.CANCELED);

                var _actualPosition = HelperPosition.Instance.GetAllPositions().FirstOrDefault();
                double newMarketPrice = _expectedOrderSent.PricePlaced * 1.25;//current price increased by 25%
                _actualPosition.UpdateCurrentMidPrice(newMarketPrice);
                var _actualOrders = _actualPosition.GetAllOrders(null);
                var _actualOrderSent = _actualOrders.FirstOrDefault();


                //ORDER SENT ASSERTION
                Assert.NotNull(_actualOrders);
                Assert.Single(_actualOrders);       //must be one order (we sent just one order, plus updates received from exchange for that same order)
                Assert.NotNull(_actualOrderSent);
                Assert.NotNull(_expectedOrderSent);

                // _expectedOrderSent   -> What VisualHFT expects as order
                // _actualOrderSent     -> The actual order the plugin has generated.
                Assert.NotEqual(0, _actualOrderSent.OrderID);
                Assert.Equal(_expectedOrderSent.OrderID, _actualOrderSent.OrderID);
                Assert.Equal(_expectedOrderSent.Status, _actualOrderSent.Status);
                Assert.Equal(_expectedOrderSent.ProviderId, _actualOrderSent.ProviderId);
                Assert.Equal(_expectedOrderSent.Symbol, _actualOrderSent.Symbol);
                Assert.Equal(_expectedOrderSent.Side, _actualOrderSent.Side);
                Assert.Equal(_expectedOrderSent.OrderType, _actualOrderSent.OrderType);
                Assert.Equal(_expectedOrderSent.PricePlaced, _actualOrderSent.PricePlaced);
                Assert.Equal(_expectedOrderSent.Quantity, _actualOrderSent.Quantity);
                Assert.Equal(_expectedOrderSent.FilledQuantity, _actualOrderSent.FilledQuantity);
                Assert.Equal(_expectedOrderSent.FilledPrice, _actualOrderSent.FilledPrice);
                Assert.Equal(_expectedOrderSent.PendingQuantity, _actualOrderSent.PendingQuantity);

                //POSITIONS STATUS: Assert the Position Manager has the same order (expected) as the actual order
                Assert.Equal(_expectedOrderSent.Symbol, _actualPosition.Symbol);
                Assert.Equal(0, _actualPosition.WrkBuy);
                Assert.Equal(0, _actualPosition.WrkSell);
                Assert.Equal(0, _actualPosition.Exposure);
                Assert.Equal(0, _actualPosition.NetPosition);
                Assert.Equal(0, _actualPosition.PLOpen);
                Assert.Equal(0, _actualPosition.PLRealized);
                Assert.Equal(0, _actualPosition.PLTot);
                Assert.Equal(0, _actualPosition.TotBuy);
                Assert.Equal(0, _actualPosition.TotSell);


                //SPECIFIC ASSERTIONS: last order received must be wit status "Canceled"
                var _actualLastOrderReceived = _actualOrders.Last();
                Assert.NotNull(_actualLastOrderReceived);
                Assert.Equal(_expectedOrderSent.OrderID, _actualLastOrderReceived.OrderID);
                Assert.Equal(eORDERSTATUS.CANCELED, _actualLastOrderReceived.Status);


                _testOutputHelper.WriteLine($"TESTING {CONNECTOR_NAME} OK");
            }
        }


        /*
        Test Case 8: Place a Stop-Limit Order
        */
        [Fact]
        public void Test_PrivateMessage_Scenario8()
        {
            //1. Place a Stop-Limit Order
            //2. The stop is triggered
            //3. Expecting a fill


            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors) //run the same test for each plugin
            {
                HelperPosition.Instance.Reset();

                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange & Act -> This will execute the private message scenario, creating the expected executed orders
                List<VisualHFT.Model.Order> _expectedExecutedOrders = null;
                try
                {
                    _expectedExecutedOrders = mktConnector.ExecutePrivateMessageScenario(eTestingPrivateMessageScenario.SCENARIO_8);
                }
                catch (ExceptionScenarioNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine(e.Message);
                    return;
                }
                    
                var _expectedOrderSent = _expectedExecutedOrders
                    .FirstOrDefault(x => x.Status == eORDERSTATUS.FILLED);

                var _actualPosition = HelperPosition.Instance.GetAllPositions().FirstOrDefault();
                double newMarketPrice = _expectedOrderSent.PricePlaced * 1.25;//current price increased by 25%
                _actualPosition.UpdateCurrentMidPrice(newMarketPrice);
                var _actualOrders = _actualPosition.GetAllOrders(null);
                var _actualOrderSent = _actualOrders.FirstOrDefault();


                //ORDER SENT ASSERTION
                Assert.NotNull(_actualOrders);
                Assert.Single(_actualOrders);       //must be one order (we sent just one order, plus updates received from exchange for that same order)
                Assert.NotNull(_actualOrderSent);
                Assert.NotNull(_expectedOrderSent);

                // _expectedOrderSent   -> What VisualHFT expects as order
                // _actualOrderSent     -> The actual order the plugin has generated.
                Assert.NotEqual(0, _actualOrderSent.OrderID);
                Assert.Equal(_expectedOrderSent.OrderID, _actualOrderSent.OrderID);
                Assert.Equal(_expectedOrderSent.Status, _actualOrderSent.Status);
                Assert.Equal(_expectedOrderSent.ProviderId, _actualOrderSent.ProviderId);
                Assert.Equal(_expectedOrderSent.Symbol, _actualOrderSent.Symbol);
                Assert.Equal(_expectedOrderSent.Side, _actualOrderSent.Side);
                Assert.Equal(_expectedOrderSent.OrderType, _actualOrderSent.OrderType);
                Assert.Equal(_expectedOrderSent.PricePlaced, _actualOrderSent.PricePlaced);
                Assert.Equal(_expectedOrderSent.Quantity, _actualOrderSent.Quantity);
                Assert.Equal(_expectedOrderSent.FilledQuantity, _actualOrderSent.FilledQuantity);
                Assert.Equal(_expectedOrderSent.FilledPrice, _actualOrderSent.FilledPrice);
                Assert.Equal(_expectedOrderSent.PendingQuantity, _actualOrderSent.PendingQuantity);

                //POSITIONS STATUS: Assert the Position Manager has the same order (expected) as the actual order
                Assert.Equal(_expectedOrderSent.Symbol, _actualPosition.Symbol);
                Assert.Equal(0, _actualPosition.WrkBuy);
                Assert.Equal(0, _actualPosition.WrkSell);
                Assert.Equal(-(_expectedOrderSent.FilledQuantity * newMarketPrice), _actualPosition.Exposure);
                Assert.Equal(-_expectedOrderSent.FilledQuantity, _actualPosition.NetPosition);
                Assert.Equal((_expectedOrderSent.FilledQuantity * (_expectedOrderSent.PricePlaced - newMarketPrice)), _actualPosition.PLOpen);
                Assert.Equal(0, _actualPosition.PLRealized);
                Assert.Equal((_expectedOrderSent.FilledQuantity * (_expectedOrderSent.PricePlaced - newMarketPrice)), _actualPosition.PLTot);
                Assert.Equal(0, _actualPosition.TotBuy);
                Assert.Equal(_expectedOrderSent.FilledQuantity, _actualPosition.TotSell);



                //SPECIFIC ASSERTIONS: last order received must be wit status "Canceled"
                var _actualLastOrderReceived = _actualOrders.Last();
                Assert.NotNull(_actualLastOrderReceived);
                Assert.Equal(_expectedOrderSent.OrderID, _actualLastOrderReceived.OrderID);
                Assert.Equal(eORDERSTATUS.FILLED, _actualLastOrderReceived.Status);


                _testOutputHelper.WriteLine($"TESTING {CONNECTOR_NAME} OK");
            }
        }


        /*
        Test Case 9: Test Network Disconnection and Reconnection
        */
        [Fact]
        public void Test_PrivateMessage_Scenario9()
        {

        }

    }

}
