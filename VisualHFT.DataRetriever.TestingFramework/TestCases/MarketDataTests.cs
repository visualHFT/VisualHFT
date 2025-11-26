using VisualHFT.Commons.Exceptions;
using VisualHFT.Model;
using VisualHFT.Commons.Model;
using VisualHFT.Helpers;
using VisualHFT.DataRetriever.TestingFramework.Core;
using VisualHFT.Enums;
using Xunit.Abstractions;

namespace VisualHFT.DataRetriever.TestingFramework.TestCases
{
    public class MarketDataTests
    {
        private readonly ITestOutputHelper _testOutputHelper;

        public MarketDataTests(ITestOutputHelper testOutputHelper)
        {
            _testOutputHelper = testOutputHelper;
        }

        private OrderBook CreateInitialSnapshot()
        {
            var _symbol = "EUR/USD";
            return new OrderBook(_symbol, 5, 5)
            {
                Asks = new CachedCollection<BookItem>(null)
                {
                    new BookItem() { Price = 1.00010, Size = 100, Symbol = _symbol, EntryID = "1", IsBid = false, },
                    new BookItem() { Price = 1.00009, Size = 100, Symbol = _symbol, EntryID = "2", IsBid = false, },
                    new BookItem() { Price = 1.00008, Size = 100, Symbol = _symbol, EntryID = "3", IsBid = false, },
                    new BookItem() { Price = 1.00007, Size = 100, Symbol = _symbol, EntryID = "4", IsBid = false, },
                    new BookItem() { Price = 1.00006, Size = 100, Symbol = _symbol, EntryID = "5", IsBid = false, },
                },
                Bids = new CachedCollection<BookItem>(null)
                {
                    new BookItem() { Price = 1.00005, Size = 100, Symbol = _symbol, EntryID = "6", IsBid = true, },
                    new BookItem() { Price = 1.00004, Size = 100, Symbol = _symbol, EntryID = "7", IsBid = true, },
                    new BookItem() { Price = 1.00003, Size = 100, Symbol = _symbol, EntryID = "8", IsBid = true, },
                    new BookItem() { Price = 1.00002, Size = 100, Symbol = _symbol, EntryID = "9", IsBid = true, },
                    new BookItem() { Price = 1.00001, Size = 100, Symbol = _symbol, EntryID = "10", IsBid = true, },
                }, 
                Sequence = 1,
            };
        }




        [Fact]
        public void Test_MarketDataSnapshot()
        {
            var errors = new List<ErrorReporting>();
            OrderBook _actualOrderBook = null;
            //Subscribe to the OrderBook to Assert
            HelperOrderBook.Instance.Subscribe(lob => {_actualOrderBook = lob;});

            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors) //run the same test for each plugin
            {
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange
                var snapshotModel = CreateInitialSnapshot();
                long _startingSequence = snapshotModel.Sequence;


                //Act
                try
                {
                    mktConnector.InjectSnapshot(snapshotModel, _startingSequence);
                    //Assert (all must remains equal)
                    Assert.NotNull(_actualOrderBook);
                    Assert.Equal(snapshotModel.Symbol, _actualOrderBook.Symbol);
                    Assert.Equal(snapshotModel.Sequence, _actualOrderBook.Sequence);
                    Assert.Equal(snapshotModel.Asks.Count(), _actualOrderBook.Asks.Count());
                    for (int i = 0; i < snapshotModel.Asks.Count(); i++)
                    {
                        Assert.Equal(snapshotModel.Asks[i].IsBid, _actualOrderBook.Asks[i].IsBid);
                        Assert.Equal(snapshotModel.Asks[i].Price, _actualOrderBook.Asks[i].Price);
                        Assert.Equal(snapshotModel.Asks[i].Size, _actualOrderBook.Asks[i].Size);
                    }
                    Assert.Equal(snapshotModel.Bids.Count(), _actualOrderBook.Bids.Count());
                    for (int i = 0; i < snapshotModel.Bids.Count(); i++)
                    {
                        Assert.Equal(snapshotModel.Bids[i].IsBid, _actualOrderBook.Bids[i].IsBid);
                        Assert.Equal(snapshotModel.Bids[i].Price, _actualOrderBook.Bids[i].Price);
                        Assert.Equal(snapshotModel.Bids[i].Size, _actualOrderBook.Bids[i].Size);
                    }
                    _testOutputHelper.WriteLine($"TESTING {CONNECTOR_NAME} OK");
                }
                catch (Xunit.Sdk.XunitException ex)
                {
                    // Capture assertion failures with the connector name.
                    errors.Add(new ErrorReporting() { Message = $"TEST FAILED: {ex.Message}", PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
                catch (Exception e)
                {
                    errors.Add(new ErrorReporting() { Message = e.Message, PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
            }
            PrintCollectedError(errors);
        }
        [Fact]
        public void Test_MarketDataDelta_DeleteExistingPrice()
        {
            var errors = new List<ErrorReporting>();

            OrderBook _actualOrderBook = null;
            //Subscribe to the OrderBook to Assert
            HelperOrderBook.Instance.Subscribe(lob => { _actualOrderBook = lob; });

            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors)
            {
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange
                var snapshotModel = CreateInitialSnapshot();
                long _startingSequence = snapshotModel.Sequence;

                var bidDeltaModel = new List<DeltaBookItem>() { new DeltaBookItem() { Symbol = snapshotModel.Symbol, IsBid = true, EntryID = "10", Price = 1.00001, MDUpdateAction = eMDUpdateAction.Delete, Sequence = ++_startingSequence  } };
                var askDeltaModel = new List<DeltaBookItem>() { new DeltaBookItem() { Symbol = snapshotModel.Symbol, IsBid = false, EntryID = "1", Price = 1.00010, MDUpdateAction = eMDUpdateAction.Delete, Sequence = ++_startingSequence  } };


                //Act
                try
                {
                    mktConnector.InjectSnapshot(snapshotModel, snapshotModel.Sequence);
                    mktConnector.InjectDeltaModel(bidDeltaModel, askDeltaModel);

                    //Assert
                    Assert.NotNull(_actualOrderBook);
                    Assert.Equal(snapshotModel.Symbol, _actualOrderBook.Symbol);
                    Assert.Equal(snapshotModel.Asks.Count() - 1, _actualOrderBook.Asks.Count());
                    Assert.Equal(snapshotModel.Bids.Count() - 1, _actualOrderBook.Bids.Count());
                    Assert.Null(_actualOrderBook.Asks.FirstOrDefault(x => x.Price == 1.00010));
                    Assert.Null(_actualOrderBook.Bids.FirstOrDefault(x => x.Price == 1.00001));

                }
                catch (ExceptionDeltasNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine($": DELTAS NOT SUPPORTED BY THIS EXCHANGE.");
                }
                catch (Xunit.Sdk.XunitException ex)
                {
                    // Capture assertion failures with the connector name.
                    errors.Add(new ErrorReporting() { Message = $"TEST FAILED: {ex.Message}", PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
                catch (Exception e)
                {
                    errors.Add(new ErrorReporting() { Message = e.Message, PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }

            }
            PrintCollectedError(errors);
        }
        [Fact]
        public void Test_MarketDataDelta_DeleteNonExistingPrice()
        {
            var errors = new List<ErrorReporting>();
            OrderBook _actualOrderBook = null;
            //Subscribe to the OrderBook to Assert
            HelperOrderBook.Instance.Subscribe(lob => { _actualOrderBook = lob; });

            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors)
            {
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange
                var snapshotModel = CreateInitialSnapshot();
                long _startingSequence = snapshotModel.Sequence;

                var bidDeltaModel = new List<DeltaBookItem>() { new DeltaBookItem() { Symbol = snapshotModel.Symbol, IsBid = true, EntryID = "11", Price = 1.00000, MDUpdateAction = eMDUpdateAction.Delete, Sequence = ++_startingSequence } };
                var askDeltaModel = new List<DeltaBookItem>() { new DeltaBookItem() { Symbol = snapshotModel.Symbol, IsBid = false, EntryID = "0", Price = 1.00011, MDUpdateAction = eMDUpdateAction.Delete, Sequence = ++_startingSequence } };


                //Act
                try
                {
                    mktConnector.InjectSnapshot(snapshotModel, snapshotModel.Sequence);
                    mktConnector.InjectDeltaModel(bidDeltaModel, askDeltaModel);

                    //Assert (all must remains equal)
                    Assert.NotNull(_actualOrderBook);
                    Assert.Equal(snapshotModel.Symbol, _actualOrderBook.Symbol);
                    Assert.Equal(_startingSequence, _actualOrderBook.Sequence);
                    Assert.Equal(snapshotModel.Asks.Count(), _actualOrderBook.Asks.Count());
                    for (int i = 0; i < snapshotModel.Asks.Count(); i++)
                    {
                        Assert.Equal(snapshotModel.Asks[i].IsBid, _actualOrderBook.Asks[i].IsBid);
                        Assert.Equal(snapshotModel.Asks[i].Price, _actualOrderBook.Asks[i].Price);
                        Assert.Equal(snapshotModel.Asks[i].Size, _actualOrderBook.Asks[i].Size);
                    }
                    Assert.Equal(snapshotModel.Bids.Count(), _actualOrderBook.Bids.Count());
                    for (int i = 0; i < snapshotModel.Bids.Count(); i++)
                    {
                        Assert.Equal(snapshotModel.Bids[i].IsBid, _actualOrderBook.Bids[i].IsBid);
                        Assert.Equal(snapshotModel.Bids[i].Price, _actualOrderBook.Bids[i].Price);
                        Assert.Equal(snapshotModel.Bids[i].Size, _actualOrderBook.Bids[i].Size);
                    }

                }
                catch (ExceptionDeltasNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine($": DELTAS NOT SUPPORTED BY THIS EXCHANGE.");
                }
                catch (Xunit.Sdk.XunitException ex)
                {
                    // Capture assertion failures with the connector name.
                    errors.Add(new ErrorReporting() { Message = $"TEST FAILED: {ex.Message}", PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
                catch (Exception e)
                {
                    errors.Add(new ErrorReporting() { Message = e.Message, PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }


            }
            PrintCollectedError(errors);
        }
        [Fact]
        public void Test_MarketDataDelta_AddAtTopConsideringMaxDepth()
        {
            var errors = new List<ErrorReporting>();
            OrderBook _actualOrderBook = null;
            //Subscribe to the OrderBook to Assert
            HelperOrderBook.Instance.Subscribe(lob => { _actualOrderBook = lob; });

            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors)
            {
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange
                var snapshotModel = CreateInitialSnapshot();
                long _startingSequence = snapshotModel.Sequence;

                var bidDeltaModel = new List<DeltaBookItem>() { new DeltaBookItem() { Symbol = snapshotModel.Symbol, IsBid = true, EntryID = "12", Price = 1.000055, Size = 1, MDUpdateAction = eMDUpdateAction.New, Sequence = ++_startingSequence } };
                var askDeltaModel = new List<DeltaBookItem>() { new DeltaBookItem() { Symbol = snapshotModel.Symbol, IsBid = false, EntryID = "13", Price = 1.000055, Size = 1, MDUpdateAction = eMDUpdateAction.New, Sequence = ++_startingSequence } };


                //Act
                try
                {
                    mktConnector.InjectSnapshot(snapshotModel, snapshotModel.Sequence);
                    _actualOrderBook.FilterBidAskByMaxDepth = true; //set to filter by max depth
                    mktConnector.InjectDeltaModel(bidDeltaModel, askDeltaModel);

                    //Assert
                    Assert.NotNull(_actualOrderBook);
                    Assert.Equal(snapshotModel.Symbol, _actualOrderBook.Symbol);
                    Assert.Equal(snapshotModel.Asks.Count(), _actualOrderBook.Asks.Count());
                    Assert.Equal(snapshotModel.Bids.Count(), _actualOrderBook.Bids.Count());

                    var bidTOP = _actualOrderBook.GetTOB(true);
                    var askTOP = _actualOrderBook.GetTOB(false);
                    //Assert top of the bid
                    Assert.Equal(bidDeltaModel.First().Price, bidTOP.Price);
                    Assert.Equal(bidDeltaModel.First().Size, bidTOP.Size);
                    //Assert top of the ask
                    Assert.Equal(askDeltaModel.First().Price, askTOP.Price);
                    Assert.Equal(askDeltaModel.First().Size, askTOP.Size);

                }
                catch (ExceptionDeltasNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine($": DELTAS NOT SUPPORTED BY THIS EXCHANGE.");
                }
                catch (Xunit.Sdk.XunitException ex)
                {
                    // Capture assertion failures with the connector name.
                    errors.Add(new ErrorReporting() { Message = $"TEST FAILED: {ex.Message}", PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
                catch (Exception e)
                {
                    errors.Add(new ErrorReporting() { Message = e.Message, PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }

            }
            PrintCollectedError(errors);
        }
        [Fact]
        public void Test_MarketDataDelta_AddAtBottomConsideringMaxDepth()
        {
            var errors = new List<ErrorReporting>();

            OrderBook _actualOrderBook = null;
            //Subscribe to the OrderBook to Assert
            HelperOrderBook.Instance.Subscribe(lob => { _actualOrderBook = lob; });
            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors)
            {
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");
                
                //Arrange
                var snapshotModel = CreateInitialSnapshot();
                long _startingSequence = snapshotModel.Sequence;

                var bidDeltaModel = new List<DeltaBookItem>() { new DeltaBookItem() { Symbol = snapshotModel.Symbol, IsBid = true, EntryID = "12", Price = 1.00000, Size = 1, MDUpdateAction = eMDUpdateAction.New, Sequence = ++_startingSequence } };
                var askDeltaModel = new List<DeltaBookItem>() { new DeltaBookItem() { Symbol = snapshotModel.Symbol, IsBid = false, EntryID = "13", Price = 1.00011, Size = 1, MDUpdateAction = eMDUpdateAction.New, Sequence = ++_startingSequence } };


                //Act
                try
                {
                    mktConnector.InjectSnapshot(snapshotModel, snapshotModel.Sequence);
                    _actualOrderBook.FilterBidAskByMaxDepth = true; //set to filter by max depth
                    mktConnector.InjectDeltaModel(bidDeltaModel, askDeltaModel);
                    //Assert
                    Assert.NotNull(_actualOrderBook);
                    Assert.Equal(snapshotModel.Symbol, _actualOrderBook.Symbol);
                    Assert.Equal(snapshotModel.Asks.Count(), _actualOrderBook.Asks.Count());
                    Assert.Equal(snapshotModel.Bids.Count(), _actualOrderBook.Bids.Count());

                    var bidTOP = _actualOrderBook.GetTOB(true);
                    var askTOP = _actualOrderBook.GetTOB(false);
                    //Assert top of the bid
                    Assert.Equal(snapshotModel.Bids.First().Price, bidTOP.Price);
                    Assert.Equal(snapshotModel.Bids.First().Size, bidTOP.Size);
                    //Assert top of the ask
                    Assert.Equal(snapshotModel.Asks.First().Price, askTOP.Price);
                    Assert.Equal(snapshotModel.Asks.First().Size, askTOP.Size);
                    //items added don't exist
                    Assert.Null(_actualOrderBook.Bids.FirstOrDefault(x => x.Price == 1.00000));
                    Assert.Null(_actualOrderBook.Asks.FirstOrDefault(x => x.Price == 1.00011));

                }
                catch (ExceptionDeltasNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine($": DELTAS NOT SUPPORTED BY THIS EXCHANGE.");
                }
                catch (Xunit.Sdk.XunitException ex)
                {
                    // Capture assertion failures with the connector name.
                    errors.Add(new ErrorReporting() { Message = $"TEST FAILED: {ex.Message}", PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
                catch (Exception e)
                {
                    errors.Add(new ErrorReporting() { Message = e.Message, PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
            }
            PrintCollectedError(errors);
        }
        [Fact]
        public void Test_MarketDataDelta_AddAtMiddleConsideringMaxDepth()
        {
            var errors = new List<ErrorReporting>();
            OrderBook _actualOrderBook = null;
            //Subscribe to the OrderBook to Assert
            HelperOrderBook.Instance.Subscribe(lob => { _actualOrderBook = lob; });

            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors)
            {
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange
                var snapshotModel = CreateInitialSnapshot();
                long _startingSequence = snapshotModel.Sequence;

                var bidDeltaModel = new List<DeltaBookItem>() { new DeltaBookItem() { Symbol = snapshotModel.Symbol, IsBid = true, EntryID = "12", Price = 1.000035, Size = 1, MDUpdateAction = eMDUpdateAction.New, Sequence = ++_startingSequence } };
                var askDeltaModel = new List<DeltaBookItem>() { new DeltaBookItem() { Symbol = snapshotModel.Symbol, IsBid = false, EntryID = "13", Price = 1.000085, Size = 1, MDUpdateAction = eMDUpdateAction.New, Sequence = ++_startingSequence } };


                //Act
                try
                {
                    mktConnector.InjectSnapshot(snapshotModel, snapshotModel.Sequence);
                    _actualOrderBook.FilterBidAskByMaxDepth = true; //set to filter by max depth
                    mktConnector.InjectDeltaModel(bidDeltaModel, askDeltaModel);

                    //Assert
                    Assert.NotNull(_actualOrderBook);
                    Assert.Equal(snapshotModel.Symbol, _actualOrderBook.Symbol);
                    Assert.Equal(snapshotModel.Asks.Count(), _actualOrderBook.Asks.Count());
                    Assert.Equal(snapshotModel.Bids.Count(), _actualOrderBook.Bids.Count());

                    var bidTOP = _actualOrderBook.GetTOB(true);
                    var askTOP = _actualOrderBook.GetTOB(false);
                    //Assert top of the bid
                    Assert.Equal(snapshotModel.Bids.First().Price, bidTOP.Price);
                    Assert.Equal(snapshotModel.Bids.First().Size, bidTOP.Size);
                    //Assert top of the ask
                    Assert.Equal(snapshotModel.Asks.First().Price, askTOP.Price);
                    Assert.Equal(snapshotModel.Asks.First().Size, askTOP.Size);
                    //items added don't exist
                    Assert.NotNull(_actualOrderBook.Bids.FirstOrDefault(x => x.Price == 1.000035));
                    Assert.NotNull(_actualOrderBook.Asks.FirstOrDefault(x => x.Price == 1.000085));

                }
                catch (ExceptionDeltasNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine($": DELTAS NOT SUPPORTED BY THIS EXCHANGE.");
                }
                catch (Xunit.Sdk.XunitException ex)
                {
                    // Capture assertion failures with the connector name.
                    errors.Add(new ErrorReporting() { Message = $"TEST FAILED: {ex.Message}", PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
                catch (Exception e)
                {
                    errors.Add(new ErrorReporting() { Message = e.Message, PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
            }
            PrintCollectedError(errors);
        }
        [Fact]
        public void Test_MarketDataDelta_Change()
        {
            var errors = new List<ErrorReporting>();

            OrderBook _actualOrderBook = null;
            //Subscribe to the OrderBook to Assert
            HelperOrderBook.Instance.Subscribe(lob => { _actualOrderBook = lob; });

            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors)
            {
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange
                var snapshotModel = CreateInitialSnapshot();
                long _startingSequence = snapshotModel.Sequence;

                var bidDeltaModel = new List<DeltaBookItem>() { new DeltaBookItem() { Symbol = snapshotModel.Symbol, IsBid = true, EntryID = "8", Price = 1.00003, Size = 99, MDUpdateAction = eMDUpdateAction.Change, Sequence = ++_startingSequence } };
                var askDeltaModel = new List<DeltaBookItem>() { new DeltaBookItem() { Symbol = snapshotModel.Symbol, IsBid = false, EntryID = "3", Price = 1.00008, Size = 99, MDUpdateAction = eMDUpdateAction.Change, Sequence = ++_startingSequence } };


                //Act
                try
                {
                    mktConnector.InjectSnapshot(snapshotModel, snapshotModel.Sequence);
                    _actualOrderBook.FilterBidAskByMaxDepth = true; //set to filter by max depth
                    mktConnector.InjectDeltaModel(bidDeltaModel, askDeltaModel);

                    //Assert
                    Assert.NotNull(_actualOrderBook);
                    Assert.Equal(snapshotModel.Symbol, _actualOrderBook.Symbol);
                    Assert.Equal(snapshotModel.Asks.Count(), _actualOrderBook.Asks.Count());
                    Assert.Equal(snapshotModel.Bids.Count(), _actualOrderBook.Bids.Count());

                    //Assert bid change
                    Assert.Equal(bidDeltaModel.First().Price, _actualOrderBook.Bids.First(x => x.Price == 1.00003).Price);
                    Assert.Equal(bidDeltaModel.First().Size, _actualOrderBook.Bids.First(x => x.Price == 1.00003).Size);
                    //Assert ask change
                    Assert.Equal(askDeltaModel.First().Price, _actualOrderBook.Asks.First(x => x.Price == 1.00008).Price);
                    Assert.Equal(askDeltaModel.First().Size, _actualOrderBook.Asks.First(x => x.Price == 1.00008).Size);
                    //items added don't exist
                    Assert.NotNull(_actualOrderBook.Bids.FirstOrDefault(x => x.Price == 1.00003));
                    Assert.NotNull(_actualOrderBook.Asks.FirstOrDefault(x => x.Price == 1.00008));

                }
                catch (ExceptionDeltasNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine($": DELTAS NOT SUPPORTED BY THIS EXCHANGE.");
                }
                catch (Xunit.Sdk.XunitException ex)
                {
                    // Capture assertion failures with the connector name.
                    errors.Add(new ErrorReporting() { Message = $"TEST FAILED: {ex.Message}", PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
                catch (Exception e)
                {
                    errors.Add(new ErrorReporting() { Message = e.Message, PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }

            }
            PrintCollectedError(errors);

        }
        [Fact]
        public void Test_MarketDataDelta_SequenceLowerThanSnapshotShouldNotBeProcessed()
        {
            var errors = new List<ErrorReporting>();
            OrderBook _actualOrderBook = null;
            //Subscribe to the OrderBook to Assert
            HelperOrderBook.Instance.Subscribe(lob => { _actualOrderBook = lob; });

            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors) //run the same test for each plugin
            {
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange
                var snapshotModel = CreateInitialSnapshot();
                long _startingSequence = 10; //start sequence of snapshot on 10 to see how delta will be applied
                snapshotModel.Sequence = _startingSequence;

                //Act
                try
                {
                    mktConnector.InjectSnapshot(snapshotModel, snapshotModel.Sequence); //snapshot created
                    mktConnector.InjectDeltaModel(new List<DeltaBookItem>()
                    {
                        new DeltaBookItem()
                        {
                            Symbol = snapshotModel.Symbol, EntryID = "6", MDUpdateAction = eMDUpdateAction.Delete,
                            IsBid = true, Price = 1.00005, Sequence = --_startingSequence
                        }
                    }, new List<DeltaBookItem>()
                    {
                        new DeltaBookItem()
                        {
                            Symbol = snapshotModel.Symbol, EntryID = "6", MDUpdateAction = eMDUpdateAction.Delete,
                            IsBid = false, Price = 1.00006, Sequence = --_startingSequence
                        }
                    }); //delta with lower sequence should be not computed

                    //Assert (no delta should have been processed, both Limit Order Books must remain the same)
                    Assert.NotNull(_actualOrderBook);
                    Assert.Equal(snapshotModel.Symbol, _actualOrderBook.Symbol);
                    Assert.Equal(snapshotModel.Sequence, _actualOrderBook.Sequence); //sequence should be the same (since no update should have been processed)
                    Assert.Equal(snapshotModel.Bids.Count(), _actualOrderBook.Bids.Count());
                    Assert.Equal(snapshotModel.Asks.Count(), _actualOrderBook.Asks.Count());

                }
                catch (ExceptionDeltasNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine($": DELTAS NOT SUPPORTED BY THIS EXCHANGE.");
                }
                catch (ExceptionSequenceNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine($": SEQUENCES NOT SUPPORTED BY THIS EXCHANGE.");
                }
                catch (Xunit.Sdk.XunitException ex)
                {
                    // Capture assertion failures with the connector name.
                    errors.Add(new ErrorReporting() { Message = $"TEST FAILED: {ex.Message}", PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
                catch (Exception e)
                {
                    errors.Add(new ErrorReporting() { Message = e.Message, PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
            }
            PrintCollectedError(errors);

        }
        [Fact]
        public void Test_MarketDataDelta_SequenceMustBeUpdated()
        {
            var errors = new List<ErrorReporting>();
            OrderBook _actualOrderBook = null;
            //Subscribe to the OrderBook to Assert
            HelperOrderBook.Instance.Subscribe(lob => { _actualOrderBook = lob; });

            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors) //run the same test for each plugin
            {
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange
                var snapshotModel = CreateInitialSnapshot();
                long _startingSequence = 10; //start sequence of snapshot on 10 to see how delta will be applied
                snapshotModel.Sequence = _startingSequence;

                //Act
                try
                {
                    mktConnector.InjectSnapshot(snapshotModel, snapshotModel.Sequence); //snapshot created
                    mktConnector.InjectDeltaModel(new List<DeltaBookItem>()
                    {
                        new DeltaBookItem() { Symbol = snapshotModel.Symbol, EntryID = "6", MDUpdateAction = eMDUpdateAction.Delete, IsBid = true, Price = 1.00005, Sequence = ++_startingSequence}
                    }, new List<DeltaBookItem>()
                    {
                        new DeltaBookItem() { Symbol = snapshotModel.Symbol, EntryID = "5", MDUpdateAction = eMDUpdateAction.Delete, IsBid = false, Price = 1.00006, Sequence = ++_startingSequence}
                    });

                    //Assert (no delta should have been processed, both Limit Order Books must remain the same)
                    Assert.NotNull(_actualOrderBook);
                    Assert.Equal(snapshotModel.Symbol, _actualOrderBook.Symbol);
                    Assert.Equal(_startingSequence, _actualOrderBook.Sequence); //sequence should be the same (since no update should have been processed)
                    Assert.Equal(snapshotModel.Bids.Count() - 1, _actualOrderBook.Bids.Count());
                    Assert.Equal(snapshotModel.Asks.Count() - 1, _actualOrderBook.Asks.Count());

                }
                catch (ExceptionDeltasNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine($": DELTAS NOT SUPPORTED BY THIS EXCHANGE.");
                }
                catch (ExceptionSequenceNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine($": SEQUENCES NOT SUPPORTED BY THIS EXCHANGE.");
                }
                catch (Xunit.Sdk.XunitException ex)
                {
                    // Capture assertion failures with the connector name.
                    errors.Add(new ErrorReporting() { Message = $"TEST FAILED: {ex.Message}", PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
                catch (Exception e)
                {
                    errors.Add(new ErrorReporting() { Message = e.Message, PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
            }
            PrintCollectedError(errors);

        }
        [Fact]
        public void Test_MarketDataDelta_SequenceGapShouldThrowException()
        {
            var errors = new List<ErrorReporting>();
            OrderBook _actualOrderBook = null;
            //Subscribe to the OrderBook to Assert
            HelperOrderBook.Instance.Subscribe(lob => { _actualOrderBook = lob; });

            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors) //run the same test for each plugin
            {
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");


                //Arrange
                var snapshotModel = CreateInitialSnapshot();
                long _startingSequence = 10; //start sequence of snapshot on 10 to see how delta will be applied
                snapshotModel.Sequence = _startingSequence;
                _startingSequence++; // add gap

                //Act
                try
                {
                    mktConnector.InjectSnapshot(snapshotModel, snapshotModel.Sequence); //snapshot created

                    bool forceEndTest = false;
                    // ✅ FIX: Accept any exception type (not just exact System.Exception)
                    var ex = Assert.ThrowsAny<Exception>(() =>
                    {
                        try
                        {
                            mktConnector.InjectDeltaModel(new List<DeltaBookItem>()
                            {
                                new DeltaBookItem() { Symbol = snapshotModel.Symbol, EntryID = "6", MDUpdateAction = eMDUpdateAction.Delete, IsBid = true, Price = 1.00005, Sequence = ++_startingSequence}
                            }, new List<DeltaBookItem>()
                            {
                                new DeltaBookItem() { Symbol = snapshotModel.Symbol, EntryID = "6", MDUpdateAction = eMDUpdateAction.Delete, IsBid = false, Price = 1.00006, Sequence = ++_startingSequence}
                            });
                        }
                        catch (ExceptionDeltasNotSupportedByExchange e)
                        {
                            forceEndTest = true;
                            throw new Exception(); //just get out of Assert.Throws block
                        }
                    });
                    
                    if (forceEndTest)
                        throw new ExceptionDeltasNotSupportedByExchange();

                    //Assert (no delta should have been processed, both Limit Order Books must remain the same)
                    Assert.NotNull(_actualOrderBook);
                    Assert.Equal(snapshotModel.Symbol, _actualOrderBook.Symbol);
                    Assert.Equal(snapshotModel.Sequence, _actualOrderBook.Sequence); //sequence should be the same (since no update should have been processed)
                    Assert.Equal(snapshotModel.Bids.Count(), _actualOrderBook.Bids.Count());
                    Assert.Equal(snapshotModel.Asks.Count(), _actualOrderBook.Asks.Count());

                }
                catch (ExceptionDeltasNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine($": DELTAS NOT SUPPORTED BY THIS EXCHANGE.");
                }
                catch (ExceptionSequenceNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine($": SEQUENCES NOT SUPPORTED BY THIS EXCHANGE.");
                }
                catch (Xunit.Sdk.XunitException ex)
                {
                    // Capture assertion failures with the connector name.
                    errors.Add(new ErrorReporting() { Message = $"TEST FAILED: {ex.Message}", PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
                catch (Exception e)
                {
                    errors.Add(new ErrorReporting() { Message = e.Message, PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }

            }
            PrintCollectedError(errors);

        }
        [Fact]
        public void Test_MarketDataDelta_DiffSymbolShouldNotBeProcessed()
        {
            var errors = new List<ErrorReporting>();
            OrderBook _actualOrderBook = null;
            //Subscribe to the OrderBook to Assert
            HelperOrderBook.Instance.Subscribe(lob => { if (_actualOrderBook != null && _actualOrderBook.Symbol != lob.Symbol) return;  _actualOrderBook = lob;} );

            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors)
            {
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                //Arrange
                var snapshotModel = CreateInitialSnapshot();
                long _startingSequence = snapshotModel.Sequence;

                var bidDeltaModel = new List<DeltaBookItem>() { new DeltaBookItem() { Symbol = "XXX/XXX", IsBid = true, EntryID = "10", Price = 1.00001, MDUpdateAction = eMDUpdateAction.Delete, Sequence = ++_startingSequence } };
                var askDeltaModel = new List<DeltaBookItem>() { new DeltaBookItem() { Symbol = "XXX/XXX", IsBid = false, EntryID = "1", Price = 1.00010, MDUpdateAction = eMDUpdateAction.Delete, Sequence = ++_startingSequence } };


                //Act
                try
                {
                    mktConnector.InjectSnapshot(snapshotModel, snapshotModel.Sequence);
                    mktConnector.InjectDeltaModel(bidDeltaModel, askDeltaModel);

                    //Assert
                    Assert.NotNull(_actualOrderBook);
                    Assert.Equal(snapshotModel.Symbol, _actualOrderBook.Symbol);
                    Assert.Equal(snapshotModel.Asks.Count(), _actualOrderBook.Asks.Count());
                    Assert.Equal(snapshotModel.Bids.Count(), _actualOrderBook.Bids.Count());
                    Assert.NotNull(_actualOrderBook.Asks.FirstOrDefault(x => x.Price == 1.00010));
                    Assert.NotNull(_actualOrderBook.Bids.FirstOrDefault(x => x.Price == 1.00001));

                }
                catch (ExceptionDeltasNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine($": DELTAS NOT SUPPORTED BY THIS EXCHANGE.");
                }
                catch (Xunit.Sdk.XunitException ex)
                {
                    // Capture assertion failures with the connector name.
                    errors.Add(new ErrorReporting() { Message = $"TEST FAILED: {ex.Message}", PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
                catch (Exception e)
                {
                    errors.Add(new ErrorReporting() { Message = e.Message, PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }


            }
            PrintCollectedError(errors);
        }
        [Fact]
        public void Test_MarketDataDelta_CombinedMultipleDeltas_UsingPriceLookup()
        {
            var errors = new List<ErrorReporting>();
            OrderBook _actualOrderBook = null;
            // Subscribe to receive OrderBook updates
            HelperOrderBook.Instance.Subscribe(lob => { _actualOrderBook = lob; });

            var marketConnectors = AssemblyLoader.LoadDataRetrievers();
            foreach (var mktConnector in marketConnectors)
            {
                var CONNECTOR_NAME = mktConnector.GetType().Name;
                _testOutputHelper.WriteLine($"TESTING IN {CONNECTOR_NAME}");

                // Arrange: Create the initial snapshot and set the starting sequence.
                var snapshotModel = CreateInitialSnapshot();
                long startingSequence = snapshotModel.Sequence;

                // --- BID DELTAS ---
                // Delete the bid at price 1.00005 (from the snapshot, originally at EntryID "6")
                // Change the bid at price 1.00004 (originally EntryID "7") to have a new size of 150
                // Add a new bid at price 1.00006 with size 50
                var bidDeltaModel = new List<DeltaBookItem>
                {
                    new DeltaBookItem
                    {
                        EntryID = "6",
                        Symbol = snapshotModel.Symbol,
                        IsBid = true,
                        Price = 1.00005,
                        MDUpdateAction = eMDUpdateAction.Delete,
                        Sequence = ++startingSequence
                    },
                    new DeltaBookItem
                    {
                        EntryID = "7",
                        Symbol = snapshotModel.Symbol,
                        IsBid = true,
                        Price = 1.00004,
                        Size = 150,
                        MDUpdateAction = eMDUpdateAction.Change,
                        Sequence = ++startingSequence
                    },
                    new DeltaBookItem
                    {
                        EntryID = "newID",
                        Symbol = snapshotModel.Symbol,
                        IsBid = true,
                        Price = 1.00006,
                        Size = 50,
                        MDUpdateAction = eMDUpdateAction.New,
                        Sequence = ++startingSequence
                    }
                };

                // --- ASK DELTAS ---
                // Delete the ask at price 1.00010 (originally EntryID "1")
                // Change the ask at price 1.00009 (originally EntryID "2") to have a new size of 120
                // Add a new ask at price 1.00005 with size 60
                var askDeltaModel = new List<DeltaBookItem>
                {
                    new DeltaBookItem
                    {
                        EntryID = "1",
                        Symbol = snapshotModel.Symbol,
                        IsBid = false,
                        Price = 1.00010,
                        MDUpdateAction = eMDUpdateAction.Delete,
                        Sequence = ++startingSequence
                    },
                    new DeltaBookItem
                    {
                        EntryID = "2",
                        Symbol = snapshotModel.Symbol,
                        IsBid = false,
                        Price = 1.00009,
                        Size = 120,
                        MDUpdateAction = eMDUpdateAction.Change,
                        Sequence = ++startingSequence
                    },
                    new DeltaBookItem
                    {
                        EntryID = "newID",
                        Symbol = snapshotModel.Symbol,
                        IsBid = false,
                        Price = 1.00005,
                        Size = 60,
                        MDUpdateAction = eMDUpdateAction.New,
                        Sequence = ++startingSequence
                    }
                };

                // Act: Inject the snapshot and then apply the combined delta changes.
                try
                {
                    mktConnector.InjectSnapshot(snapshotModel, snapshotModel.Sequence);
                    mktConnector.InjectDeltaModel(bidDeltaModel, askDeltaModel);


                    // Assert: Verify the overall OrderBook properties.
                    Assert.NotNull(_actualOrderBook);
                    Assert.Equal(snapshotModel.Symbol, _actualOrderBook.Symbol);

                    // --- BID ASSERTIONS ---
                    // Since we deleted one bid and added one new bid, the total count should remain unchanged.
                    Assert.Equal(snapshotModel.Bids.Count(), _actualOrderBook.Bids.Count());
                    // Verify the bid at price 1.00005 is removed.
                    Assert.Null(_actualOrderBook.Bids.FirstOrDefault(b => b.Price == 1.00005));
                    // Verify the bid at price 1.00004 has been updated to size 150.
                    var updatedBid = _actualOrderBook.Bids.FirstOrDefault(b => b.Price == 1.00004);
                    Assert.NotNull(updatedBid);
                    Assert.Equal(150, updatedBid.Size);
                    // Verify the new bid at price 1.00006 with size 50 exists.
                    var newBid = _actualOrderBook.Bids.FirstOrDefault(b => b.Price == 1.00006);
                    Assert.NotNull(newBid);
                    Assert.Equal(50, newBid.Size);

                    // --- ASK ASSERTIONS ---
                    // Since we deleted one ask and added one new ask, the total count should remain unchanged.
                    Assert.Equal(snapshotModel.Asks.Count(), _actualOrderBook.Asks.Count());
                    // Verify the ask at price 1.00010 is removed.
                    Assert.Null(_actualOrderBook.Asks.FirstOrDefault(a => a.Price == 1.00010));
                    // Verify the ask at price 1.00009 has been updated to size 120.
                    var updatedAsk = _actualOrderBook.Asks.FirstOrDefault(a => a.Price == 1.00009);
                    Assert.NotNull(updatedAsk);
                    Assert.Equal(120, updatedAsk.Size);
                    // Verify the new ask at price 1.00005 with size 60 exists.
                    var newAsk = _actualOrderBook.Asks.FirstOrDefault(a => a.Price == 1.00005);
                    Assert.NotNull(newAsk);
                    Assert.Equal(60, newAsk.Size);

                    // --- TOP OF BOOK ASSERTIONS ---
                    // For bids, the best (highest) bid should now be the new bid at 1.00006.
                    var bestBid = _actualOrderBook.GetTOB(true);
                    Assert.Equal(1.00006, bestBid.Price);
                    Assert.Equal(50, bestBid.Size);
                    // For asks, the best (lowest) ask should now be the new ask at 1.00005.
                    var bestAsk = _actualOrderBook.GetTOB(false);
                    Assert.Equal(1.00005, bestAsk.Price);
                    Assert.Equal(60, bestAsk.Size);

                    // Verify that the OrderBook's sequence number has been updated to the last delta's sequence.
                    Assert.Equal(startingSequence, _actualOrderBook.Sequence);

                }
                catch (ExceptionDeltasNotSupportedByExchange e)
                {
                    _testOutputHelper.WriteLine($": DELTAS NOT SUPPORTED BY THIS EXCHANGE.");
                }
                catch (Xunit.Sdk.XunitException ex)
                {
                    // Capture assertion failures with the connector name.
                    errors.Add(new ErrorReporting() { Message = $"TEST FAILED: {ex.Message}", PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }
                catch (Exception e)
                {
                    errors.Add(new ErrorReporting() { Message = e.Message, PluginName = CONNECTOR_NAME, MessageType = ErrorMessageTypes.ERROR });
                }

            }
            PrintCollectedError(errors);
        }


        private void PrintCollectedError(List<ErrorReporting> errors)
        {
            if (errors.Any())
            {
                string errorReport = "";
                foreach (var pluginName in errors.Select(x => x.PluginName).Distinct())
                {
                    errorReport += pluginName + ":" + Environment.NewLine + "\t" +
                                   string.Join(Environment.NewLine, errors.Where(x => x.PluginName == pluginName)
                                       .Select(x => $"[{x.MessageType.ToString()}] - {x.Message}"))
                                   + Environment.NewLine;
                }

                _testOutputHelper.WriteLine(Environment.NewLine + "Aggregate error report:" + Environment.NewLine + Environment.NewLine + errorReport);
                if (errors.Any(x => x.MessageType == ErrorMessageTypes.ERROR))
                    Assert.Fail(errorReport);
            }

        }

    }


}
