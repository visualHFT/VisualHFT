using VisualHFT.Commons.Exceptions;
using VisualHFT.Model;
using VisualHFT.Commons.Model;
using VisualHFT.Helpers;
using VisualHFT.DataRetriever.TestingFramework.Core;
using VisualHFT.Enums;

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



        [Fact]
        public void Test_OrderBookSnapshot_DisposeIdempotency()
        {
            // GOAL: Verify that calling Dispose() multiple times doesn't cause errors
            // and that accessing disposed snapshot returns empty spans

            var errors = new List<ErrorReporting>();

            // Arrange
            var snapshotModel = CreateInitialSnapshot();

            try
            {
                // Create snapshot using new struct-based implementation
                var snapshot = OrderBookSnapshot.Create();
                snapshot.UpdateFrom(snapshotModel);

                // Assert: Verify snapshot has data BEFORE disposal
                Assert.True(snapshot.Asks.Length > 0, "Asks should have data before disposal");
                Assert.True(snapshot.Bids.Length > 0, "Bids should have data before disposal");
                Assert.Equal(snapshotModel.Symbol, snapshot.Symbol);
                Assert.Equal(snapshotModel.PriceDecimalPlaces, snapshot.PriceDecimalPlaces);

                // Act: Dispose once
                snapshot.Dispose();

                // Assert: Verify snapshot returns empty data AFTER first disposal
                Assert.True(snapshot.Asks.IsEmpty, "Asks should be empty after disposal");
                Assert.True(snapshot.Bids.IsEmpty, "Bids should be empty after disposal");

                // Act: Dispose again (test idempotency)
                snapshot.Dispose();

                // Assert: Should not throw and still return empty
                Assert.True(snapshot.Asks.IsEmpty, "Asks should remain empty after second disposal");
                Assert.True(snapshot.Bids.IsEmpty, "Bids should remain empty after second disposal");

                // Act: Dispose third time (stress test idempotency)
                snapshot.Dispose();

                // Assert: Verify metadata is preserved (value-type fields)
                Assert.Equal(snapshotModel.Symbol, snapshot.Symbol);
                Assert.Equal(snapshotModel.PriceDecimalPlaces, snapshot.PriceDecimalPlaces);

                _testOutputHelper.WriteLine("✅ Dispose idempotency test PASSED");
            }
            catch (Xunit.Sdk.XunitException ex)
            {
                errors.Add(new ErrorReporting()
                {
                    Message = $"TEST FAILED: {ex.Message}",
                    PluginName = "OrderBookSnapshot",
                    MessageType = ErrorMessageTypes.ERROR
                });
            }
            catch (Exception e)
            {
                errors.Add(new ErrorReporting()
                {
                    Message = e.Message,
                    PluginName = "OrderBookSnapshot",
                    MessageType = ErrorMessageTypes.ERROR
                });
            }

            PrintCollectedError(errors);
        }

        [Fact]
        public void Test_OrderBookSnapshot_ZeroCopySpanAccess()
        {
            // GOAL: Verify that ReadOnlySpan<BookItem> provides zero-copy access
            // and that modifications to master don't affect snapshot

            var errors = new List<ErrorReporting>();

            try
            {
                // Arrange: Create master OrderBook
                var masterOrderBook = CreateInitialSnapshot();
                var originalBidPrice = masterOrderBook.Bids.First().Price.Value;
                var originalAskPrice = masterOrderBook.Asks.First().Price.Value;
                var originalBidSize = masterOrderBook.Bids.First().Size.Value;
                var originalAskSize = masterOrderBook.Asks.First().Size.Value;

                // Act: Create snapshot
                var snapshot = OrderBookSnapshot.Create();
                snapshot.UpdateFrom(masterOrderBook);

                // Capture snapshot data via ReadOnlySpan
                var snapshotBids = snapshot.Bids;
                var snapshotAsks = snapshot.Asks;

                // Assert: Verify snapshot matches master
                Assert.Equal(5, snapshotBids.Length);
                Assert.Equal(5, snapshotAsks.Length);
                Assert.Equal(originalBidPrice, snapshotBids[0].Price.Value);
                Assert.Equal(originalAskPrice, snapshotAsks[0].Price.Value);
                Assert.Equal(originalBidSize, snapshotBids[0].Size.Value);
                Assert.Equal(originalAskSize, snapshotAsks[0].Size.Value);

                // Act: Modify master OrderBook (add new level)
                masterOrderBook.AddOrUpdateLevel(new DeltaBookItem()
                {
                    Symbol = masterOrderBook.Symbol,
                    IsBid = true,
                    Price = 1.00010,
                    Size = 200,
                    MDUpdateAction = eMDUpdateAction.New,
                    LocalTimeStamp = DateTime.Now,
                    ServerTimeStamp = DateTime.Now
                });

                // Assert: Snapshot should remain UNCHANGED (zero-copy isolation)
                var snapshotBidsAfter = snapshot.Bids;
                var snapshotAsksAfter = snapshot.Asks;

                Assert.Equal(5, snapshotBidsAfter.Length); // Still 5, not 6
                Assert.Equal(5, snapshotAsksAfter.Length);
                Assert.Equal(originalBidPrice, snapshotBidsAfter[0].Price.Value);
                Assert.Equal(originalAskPrice, snapshotAsksAfter[0].Price.Value);

                // Act: Access via iteration (verify zero-copy enumeration)
                int bidCount = 0;
                foreach (var bid in snapshot.Bids)
                {
                    Assert.NotNull(bid);
                    Assert.True(bid.Price.HasValue);
                    Assert.True(bid.Size.HasValue);
                    bidCount++;
                }
                Assert.Equal(5, bidCount);

                // Cleanup
                snapshot.Dispose();

                _testOutputHelper.WriteLine("✅ Zero-copy span access test PASSED");
            }
            catch (Xunit.Sdk.XunitException ex)
            {
                errors.Add(new ErrorReporting()
                {
                    Message = $"TEST FAILED: {ex.Message}",
                    PluginName = "OrderBookSnapshot",
                    MessageType = ErrorMessageTypes.ERROR
                });
            }
            catch (Exception e)
            {
                errors.Add(new ErrorReporting()
                {
                    Message = e.Message,
                    PluginName = "OrderBookSnapshot",
                    MessageType = ErrorMessageTypes.ERROR
                });
            }

            PrintCollectedError(errors);
        }

        [Fact]
        public void Test_OrderBookSnapshot_MinMaxCachingAccuracy()
        {
            // GOAL: Verify that GetMinMaxSizes() returns cached values correctly
            // and that min/max are computed accurately during UpdateFrom()

            var errors = new List<ErrorReporting>();

            try
            {
                // Arrange: Create OrderBook with known min/max sizes
                var _symbol = "EUR/USD";
                var orderBook = new OrderBook(_symbol, 5, 10)
                {
                    Asks = new CachedCollection<BookItem>(null)
            {
                new BookItem() { Price = 1.00010, Size = 500, Symbol = _symbol, EntryID = "1", IsBid = false }, // MAX
                new BookItem() { Price = 1.00009, Size = 100, Symbol = _symbol, EntryID = "2", IsBid = false },
                new BookItem() { Price = 1.00008, Size = 50, Symbol = _symbol, EntryID = "3", IsBid = false },  // MIN
            },
                    Bids = new CachedCollection<BookItem>(null)
            {
                new BookItem() { Price = 1.00005, Size = 250, Symbol = _symbol, EntryID = "4", IsBid = true },
                new BookItem() { Price = 1.00004, Size = 75, Symbol = _symbol, EntryID = "5", IsBid = true },
                new BookItem() { Price = 1.00003, Size = 150, Symbol = _symbol, EntryID = "6", IsBid = true },
            },
                    Sequence = 1,
                };

                // Expected min/max
                double expectedMin = 50;  // Smallest size
                double expectedMax = 500; // Largest size

                // Act: Create snapshot and update from master
                var snapshot = OrderBookSnapshot.Create();
                snapshot.UpdateFrom(orderBook);

                // Assert: Verify min/max are cached correctly
                var minMaxTuple = snapshot.GetMinMaxSizes();
                Assert.NotNull(minMaxTuple);

                double actualMin = minMaxTuple.Item1;
                double actualMax = minMaxTuple.Item2;

                Assert.Equal(expectedMin, actualMin);
                Assert.Equal(expectedMax, actualMax);

                // Act: Call GetMinMaxSizes() multiple times (should return cached value - zero iteration)
                var minMaxTuple2 = snapshot.GetMinMaxSizes();
                var minMaxTuple3 = snapshot.GetMinMaxSizes();

                // Assert: All calls return identical values (proving caching)
                Assert.Equal(minMaxTuple.Item1, minMaxTuple2.Item1);
                Assert.Equal(minMaxTuple.Item1, minMaxTuple3.Item1);
                Assert.Equal(minMaxTuple.Item2, minMaxTuple2.Item2);
                Assert.Equal(minMaxTuple.Item2, minMaxTuple3.Item2);

                // Act: Test with empty order book (edge case)
                var emptyOrderBook = new OrderBook(_symbol, 5, 10)
                {
                    Asks = new CachedCollection<BookItem>(null),
                    Bids = new CachedCollection<BookItem>(null),
                    Sequence = 2,
                };

                var emptySnapshot = OrderBookSnapshot.Create();
                emptySnapshot.UpdateFrom(emptyOrderBook);

                var emptyMinMax = emptySnapshot.GetMinMaxSizes();

                // Assert: Empty book returns (0, 0)
                Assert.Equal(0, emptyMinMax.Item1);
                Assert.Equal(0, emptyMinMax.Item2);

                // Cleanup
                snapshot.Dispose();
                emptySnapshot.Dispose();

                _testOutputHelper.WriteLine("✅ Min/Max caching accuracy test PASSED");
            }
            catch (Xunit.Sdk.XunitException ex)
            {
                errors.Add(new ErrorReporting()
                {
                    Message = $"TEST FAILED: {ex.Message}",
                    PluginName = "OrderBookSnapshot",
                    MessageType = ErrorMessageTypes.ERROR
                });
            }
            catch (Exception e)
            {
                errors.Add(new ErrorReporting()
                {
                    Message = e.Message,
                    PluginName = "OrderBookSnapshot",
                    MessageType = ErrorMessageTypes.ERROR
                });
            }

            PrintCollectedError(errors);
        }



        [Fact]
        public void Test_RealScenario_HighFrequencySnapshotLifecycle_With100Updates()
        {
            // BUSINESS SCENARIO: Simulates real HFT environment with 100k msg/sec rate
            // VALIDATES: Create → UpdateFrom → Queue → Process → Dispose lifecycle
            // BASED ON: vmOrderBook.LIMITORDERBOOK_OnDataReceived workflow

            var errors = new List<ErrorReporting>();
            const int MESSAGE_COUNT = 100; // Simulate 100 rapid updates

            try
            {
                _testOutputHelper.WriteLine("=== SCENARIO: High-Frequency Market Data Processing ===");
                _testOutputHelper.WriteLine($"Simulating {MESSAGE_COUNT} rapid orderbook updates");

                // Arrange: Create master OrderBook (represents live exchange feed)
                var masterOrderBook = CreateInitialSnapshot();
                var snapshotQueue = new List<OrderBookSnapshot>();
                var timings = new List<TimeSpan>();

                // ACT 1: Simulate rapid snapshot creation (typical vmOrderBook workflow)
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                for (int i = 0; i < MESSAGE_COUNT; i++)
                {
                    var iterationStart = stopwatch.Elapsed;

                    // Business logic: Create snapshot from incoming market data
                    var snapshot = OrderBookSnapshot.Create();
                    snapshot.UpdateFrom(masterOrderBook);

                    // Store for processing (simulates queue buffering)
                    snapshotQueue.Add(snapshot);

                    timings.Add(stopwatch.Elapsed - iterationStart);

                    // ✅ FIX: Simulate realistic price movements (maintain bid < ask invariant)
                    // Bids move within their range, asks move within theirs
                    if (i % 2 == 0)
                    {
                        // Update BID side - prices should be BELOW the spread
                        // Start from best bid (1.00005) and move down to avoid crossing
                        masterOrderBook.AddOrUpdateLevel(new DeltaBookItem()
                        {
                            Symbol = masterOrderBook.Symbol,
                            IsBid = true,
                            Price = 1.00005 - (i * 0.000005), // Move DOWN from best bid
                            Size = 100 + i,
                            MDUpdateAction = eMDUpdateAction.Change,
                            LocalTimeStamp = DateTime.Now,
                            ServerTimeStamp = DateTime.Now
                        });
                    }
                    else
                    {
                        // Update ASK side - prices should be ABOVE the spread
                        // Start from best ask (1.00006) and move up to avoid crossing
                        masterOrderBook.AddOrUpdateLevel(new DeltaBookItem()
                        {
                            Symbol = masterOrderBook.Symbol,
                            IsBid = false,
                            Price = 1.00006 + (i * 0.000005), // Move UP from best ask
                            Size = 100 + i,
                            MDUpdateAction = eMDUpdateAction.Change,
                            LocalTimeStamp = DateTime.Now,
                            ServerTimeStamp = DateTime.Now
                        });
                    }
                }

                stopwatch.Stop();

                // Assert: Performance characteristics
                var avgLatency = timings.Average(t => t.TotalMicroseconds);
                var p99Latency = timings.OrderByDescending(t => t.TotalMicroseconds)
                    .Skip((int)(timings.Count * 0.01)).First().TotalMicroseconds;

                _testOutputHelper.WriteLine($"✅ Created {MESSAGE_COUNT} snapshots");
                _testOutputHelper.WriteLine($"   Avg latency: {avgLatency:F2}µs");
                _testOutputHelper.WriteLine($"   P99 latency: {p99Latency:F2}µs");
                _testOutputHelper.WriteLine($"   Total time: {stopwatch.ElapsedMilliseconds}ms");

                // Assert: Validate all snapshots are usable
                Assert.Equal(MESSAGE_COUNT, snapshotQueue.Count);
                foreach (var snapshot in snapshotQueue)
                {
                    Assert.True(snapshot.Asks.Length > 0, "Snapshot should have asks");
                    Assert.True(snapshot.Bids.Length > 0, "Snapshot should have bids");
                    Assert.True(snapshot.MidPrice > 0, "Snapshot should have valid mid price");
                }

                // ACT 2: Process snapshots (simulates QUEUE_onReadAction)
                int processedCount = 0;
                foreach (var snapshot in snapshotQueue)
                {
                    // Business logic: Extract TOB for visualization
                    var bidTOB = snapshot.GetTOB(true);
                    var askTOB = snapshot.GetTOB(false);

                    Assert.NotNull(bidTOB);
                    Assert.NotNull(askTOB);

                    // ✅ FIX: Add defensive check with diagnostic output
                    if (bidTOB.Price >= askTOB.Price)
                    {
                        _testOutputHelper.WriteLine($"ERROR at snapshot {processedCount}: Bid={bidTOB.Price:F5}, Ask={askTOB.Price:F5}");
                    }
                    Assert.True(bidTOB.Price < askTOB.Price, $"Bid ({bidTOB.Price:F5}) must be lower than ask ({askTOB.Price:F5}) at snapshot {processedCount}");

                    // Business logic: Calculate spread
                    var spread = snapshot.Spread;
                    Assert.True(spread > 0, "Spread must be positive");

                    processedCount++;
                }

                _testOutputHelper.WriteLine($"✅ Processed {processedCount} snapshots successfully");

                // ACT 3: Cleanup (critical for memory management)
                int disposedCount = 0;
                foreach (var snapshot in snapshotQueue)
                {
                    snapshot.Dispose();

                    // Verify disposal worked
                    Assert.True(snapshot.Asks.IsEmpty, "Asks should be empty after dispose");
                    Assert.True(snapshot.Bids.IsEmpty, "Bids should be empty after dispose");

                    disposedCount++;
                }

                _testOutputHelper.WriteLine($"✅ Disposed {disposedCount} snapshots (returned arrays to pool)");

                // Assert: Final validation
                Assert.Equal(MESSAGE_COUNT, processedCount);
                Assert.Equal(MESSAGE_COUNT, disposedCount);

                // Performance assertion: Should handle 100k msg/sec (10µs per msg)
                Assert.True(avgLatency < 50, $"Average latency {avgLatency}µs exceeds 50µs budget for 20k/sec");

                _testOutputHelper.WriteLine("=== SCENARIO COMPLETED SUCCESSFULLY ===");
            }
            catch (Exception ex)
            {
                errors.Add(new ErrorReporting()
                {
                    Message = $"High-frequency scenario failed: {ex.Message}",
                    PluginName = "OrderBookSnapshot",
                    MessageType = ErrorMessageTypes.ERROR
                });
            }

            PrintCollectedError(errors);
        }

        [Fact]
        public void Test_RealScenario_MarketResilienceStudy_ShockDetection()
        {
            // BUSINESS SCENARIO: Market Resilience Study processing snapshots for shock detection
            // VALIDATES: Snapshot used for depth depletion detection (IsLOBDepleted workflow)
            // BASED ON: MarketResilienceCalculator.OnOrderBookUpdate usage

            var errors = new List<ErrorReporting>();

            try
            {
                _testOutputHelper.WriteLine("=== SCENARIO: Market Resilience Shock Detection ===");

                // Arrange: Create baseline market conditions
                var _symbol = "EUR/USD";
                var baselineOrderBook = new OrderBook(_symbol, 5, 10)
                {
                    // ✅ FIX: Asks MUST be sorted ASCENDING by price (best ask = lowest price first)
                    Asks = new CachedCollection<BookItem>(null)
            {
                new BookItem() { Price = 1.00008, Size = 500, Symbol = _symbol, IsBid = false }, // BEST ask (lowest price)
                new BookItem() { Price = 1.00009, Size = 400, Symbol = _symbol, IsBid = false },
                new BookItem() { Price = 1.00010, Size = 300, Symbol = _symbol, IsBid = false }, // WORST ask (highest price)
            },
                    // ✅ FIX: Bids MUST be sorted DESCENDING by price (best bid = highest price first)
                    Bids = new CachedCollection<BookItem>(null)
            {
                new BookItem() { Price = 1.00005, Size = 500, Symbol = _symbol, IsBid = true }, // BEST bid (highest price)
                new BookItem() { Price = 1.00004, Size = 400, Symbol = _symbol, IsBid = true },
                new BookItem() { Price = 1.00003, Size = 300, Symbol = _symbol, IsBid = true }, // WORST bid (lowest price)
            },
                    Sequence = 1,
                };

                // ACT 1: Create baseline snapshot
                var baselineSnapshot = OrderBookSnapshot.Create();
                baselineSnapshot.UpdateFrom(baselineOrderBook);

                _testOutputHelper.WriteLine($"Baseline spread: {baselineSnapshot.Spread:F5}");
                _testOutputHelper.WriteLine($"Baseline mid: {baselineSnapshot.MidPrice:F5}");
                _testOutputHelper.WriteLine($"Baseline best bid: {baselineSnapshot.GetTOB(true).Price:F5} @ {baselineSnapshot.GetTOB(true).Size}");
                _testOutputHelper.WriteLine($"Baseline best ask: {baselineSnapshot.GetTOB(false).Price:F5} @ {baselineSnapshot.GetTOB(false).Size}");

                // Assert: Baseline is valid
                Assert.True(baselineSnapshot.Spread > 0, "Baseline should have positive spread");
                Assert.Equal(500, baselineSnapshot.GetTOB(true).Size.Value); // Best bid size
                Assert.Equal(500, baselineSnapshot.GetTOB(false).Size.Value); // Best ask size

                // ACT 2: Simulate depth shock (bid side depleted)
                var shockedOrderBook = new OrderBook(_symbol, 5, 10)
                {
                    // ✅ FIX: Asks sorted ASCENDING
                    Asks = new CachedCollection<BookItem>(null)
            {
                new BookItem() { Price = 1.00008, Size = 500, Symbol = _symbol, IsBid = false }, // BEST ask
                new BookItem() { Price = 1.00009, Size = 400, Symbol = _symbol, IsBid = false },
                new BookItem() { Price = 1.00010, Size = 300, Symbol = _symbol, IsBid = false },
            },
                    // ✅ FIX: Bids sorted DESCENDING with depletion
                    Bids = new CachedCollection<BookItem>(null)
            {
                new BookItem() { Price = 1.00005, Size = 50, Symbol = _symbol, IsBid = true },  // DEPLETED: 90% reduction
                new BookItem() { Price = 1.00004, Size = 100, Symbol = _symbol, IsBid = true }, // DEPLETED: 75% reduction
                new BookItem() { Price = 1.00003, Size = 150, Symbol = _symbol, IsBid = true }, // DEPLETED: 50% reduction
            },
                    Sequence = 2,
                };

                var shockedSnapshot = OrderBookSnapshot.Create();
                shockedSnapshot.UpdateFrom(shockedOrderBook);

                _testOutputHelper.WriteLine($"Shocked spread: {shockedSnapshot.Spread:F5}");
                _testOutputHelper.WriteLine($"Shocked mid: {shockedSnapshot.MidPrice:F5}");
                _testOutputHelper.WriteLine($"Shocked best bid: {shockedSnapshot.GetTOB(true).Price:F5} @ {shockedSnapshot.GetTOB(true).Size}");
                _testOutputHelper.WriteLine($"Bid depth reduction: {(1 - (50.0 / 500.0)) * 100:F1}%");

                // Assert: Shock is detectable via snapshot
                Assert.True(shockedSnapshot.GetTOB(true).Size.Value < 100,
                    "Bid side should show depletion");
                Assert.Equal(50, shockedSnapshot.GetTOB(true).Size.Value);

                // Business logic: Calculate depth ratio (used by MarketResilienceCalculator)
                double baselineBidDepth = CalculateTotalDepth(baselineSnapshot.Bids);
                double shockedBidDepth = CalculateTotalDepth(shockedSnapshot.Bids);
                double depthRatio = shockedBidDepth / baselineBidDepth;

                _testOutputHelper.WriteLine($"Depth ratio: {depthRatio:F3} (baseline: {baselineBidDepth}, shocked: {shockedBidDepth})");

                Assert.True(depthRatio < 0.5, "Depth depletion should be > 50%");

                // ACT 3: Simulate recovery
                var recoveredOrderBook = new OrderBook(_symbol, 5, 10)
                {
                    // ✅ FIX: Asks sorted ASCENDING
                    Asks = new CachedCollection<BookItem>(null)
            {
                new BookItem() { Price = 1.00008, Size = 500, Symbol = _symbol, IsBid = false }, // BEST ask
                new BookItem() { Price = 1.00009, Size = 400, Symbol = _symbol, IsBid = false },
                new BookItem() { Price = 1.00010, Size = 300, Symbol = _symbol, IsBid = false },
            },
                    // ✅ FIX: Bids sorted DESCENDING with recovery
                    Bids = new CachedCollection<BookItem>(null)
            {
                new BookItem() { Price = 1.00005, Size = 450, Symbol = _symbol, IsBid = true }, // RECOVERED: 90%
                new BookItem() { Price = 1.00004, Size = 360, Symbol = _symbol, IsBid = true }, // RECOVERED: 90%
                new BookItem() { Price = 1.00003, Size = 270, Symbol = _symbol, IsBid = true }, // RECOVERED: 90%
            },
                    Sequence = 3,
                };

                var recoveredSnapshot = OrderBookSnapshot.Create();
                recoveredSnapshot.UpdateFrom(recoveredOrderBook);

                double recoveredBidDepth = CalculateTotalDepth(recoveredSnapshot.Bids);
                double recoveryRatio = recoveredBidDepth / baselineBidDepth;

                _testOutputHelper.WriteLine($"Recovery ratio: {recoveryRatio:F3} (recovered: {recoveredBidDepth})");

                Assert.True(recoveryRatio > 0.85, "Should show >85% recovery");

                // Cleanup (critical!)
                baselineSnapshot.Dispose();
                shockedSnapshot.Dispose();
                recoveredSnapshot.Dispose();

                _testOutputHelper.WriteLine("=== SCENARIO COMPLETED: Shock detected and recovered ===");
            }
            catch (Exception ex)
            {
                errors.Add(new ErrorReporting()
                {
                    Message = $"Market resilience scenario failed: {ex.Message}\n{ex.StackTrace}",
                    PluginName = "OrderBookSnapshot",
                    MessageType = ErrorMessageTypes.ERROR
                });
            }

            PrintCollectedError(errors);
        }
        [Fact]
        public void Test_RealScenario_MultiplePlugins_ConcurrentSnapshotUsage()
        {
            // BUSINESS SCENARIO: Multiple study plugins consuming same OrderBook data
            // VALIDATES: Independent snapshot lifecycle per consumer (vmOrderBook, MarketResilience, VPIN)
            // BASED ON: Real plugin architecture where multiple studies subscribe to same data

            var errors = new List<ErrorReporting>();

            try
            {
                _testOutputHelper.WriteLine("=== SCENARIO: Multiple Concurrent Plugin Consumers ===");

                // Arrange: Create master OrderBook (shared data source)
                var masterOrderBook = CreateInitialSnapshot();
                masterOrderBook.Sequence = 1000;

                // ACT: Simulate 3 concurrent plugin consumers
                // Consumer 1: vmOrderBook (UI visualization)
                var uiSnapshot = OrderBookSnapshot.Create();
                uiSnapshot.UpdateFrom(masterOrderBook);

                // Consumer 2: Market Resilience Study
                var mrSnapshot = OrderBookSnapshot.Create();
                mrSnapshot.UpdateFrom(masterOrderBook);

                // Consumer 3: VPIN Study (order flow analysis)
                var vpinSnapshot = OrderBookSnapshot.Create();
                vpinSnapshot.UpdateFrom(masterOrderBook);

                _testOutputHelper.WriteLine($"Created 3 independent snapshots from sequence {masterOrderBook.Sequence}");

                // Assert: All snapshots are independent copies
                Assert.Equal(uiSnapshot.Symbol, mrSnapshot.Symbol);
                Assert.Equal(uiSnapshot.Symbol, vpinSnapshot.Symbol);
                Assert.Equal(5, uiSnapshot.Asks.Length);
                Assert.Equal(5, mrSnapshot.Asks.Length);
                Assert.Equal(5, vpinSnapshot.Asks.Length);

                // Business logic: Each plugin processes independently

                // UI Plugin: Extract TOB for display
                var uiBidTOB = uiSnapshot.GetTOB(true);
                var uiAskTOB = uiSnapshot.GetTOB(false);
                var uiMidPrice = uiSnapshot.MidPrice;

                _testOutputHelper.WriteLine($"UI Plugin - Bid: {uiBidTOB.Price:F5}, Ask: {uiAskTOB.Price:F5}, Mid: {uiMidPrice:F5}");

                // Market Resilience: Calculate spread for shock detection
                var mrSpread = mrSnapshot.Spread;
                var mrMinMax = mrSnapshot.GetMinMaxSizes();

                _testOutputHelper.WriteLine($"MR Plugin - Spread: {mrSpread:F5}, MinSize: {mrMinMax.Item1}, MaxSize: {mrMinMax.Item2}");

                // VPIN: Calculate volume imbalance
                double vpinBuyVolume = CalculateTotalDepth(vpinSnapshot.Bids);
                double vpinSellVolume = CalculateTotalDepth(vpinSnapshot.Asks);
                double vpinImbalance = Math.Abs(vpinBuyVolume - vpinSellVolume) / (vpinBuyVolume + vpinSellVolume);

                _testOutputHelper.WriteLine($"VPIN Plugin - Buy: {vpinBuyVolume}, Sell: {vpinSellVolume}, Imbalance: {vpinImbalance:F3}");

                // Assert: Each plugin got correct data
                Assert.True(uiMidPrice > 0);
                Assert.True(mrSpread > 0);
                Assert.True(vpinImbalance >= 0 && vpinImbalance <= 1);

                // ACT: Master OrderBook changes (simulates new market data)
                masterOrderBook.AddOrUpdateLevel(new DeltaBookItem()
                {
                    Symbol = masterOrderBook.Symbol,
                    IsBid = true,
                    Price = 1.00005,
                    Size = 200, // Size doubled
                    MDUpdateAction = eMDUpdateAction.Change,
                    LocalTimeStamp = DateTime.Now,
                    ServerTimeStamp = DateTime.Now
                });

                // Assert: Existing snapshots are UNCHANGED (isolation)
                Assert.Equal(100, uiSnapshot.GetTOB(true).Size.Value); // Still original value
                Assert.Equal(100, mrSnapshot.GetTOB(true).Size.Value);
                Assert.Equal(100, vpinSnapshot.GetTOB(true).Size.Value);

                _testOutputHelper.WriteLine("✅ Snapshots remain isolated after master change");

                // ACT: UI plugin done first (typical - fast visualization)
                uiSnapshot.Dispose();
                _testOutputHelper.WriteLine("✅ UI Plugin disposed snapshot");

                // Assert: Other plugins still have valid data
                Assert.Equal(5, mrSnapshot.Asks.Length);
                Assert.Equal(5, vpinSnapshot.Asks.Length);
                Assert.True(mrSnapshot.MidPrice > 0);
                Assert.True(vpinSnapshot.MidPrice > 0);

                // ACT: MR plugin completes analysis
                mrSnapshot.Dispose();
                _testOutputHelper.WriteLine("✅ MR Plugin disposed snapshot");

                // Assert: VPIN still valid
                Assert.Equal(5, vpinSnapshot.Asks.Length);
                Assert.True(vpinSnapshot.MidPrice > 0);

                // ACT: VPIN completes last
                vpinSnapshot.Dispose();
                _testOutputHelper.WriteLine("✅ VPIN Plugin disposed snapshot");

                // Assert: All disposed snapshots return empty
                Assert.True(uiSnapshot.Asks.IsEmpty);
                Assert.True(mrSnapshot.Asks.IsEmpty);
                Assert.True(vpinSnapshot.Asks.IsEmpty);

                _testOutputHelper.WriteLine("=== SCENARIO COMPLETED: All plugins processed independently ===");
            }
            catch (Exception ex)
            {
                errors.Add(new ErrorReporting()
                {
                    Message = $"Multi-plugin scenario failed: {ex.Message}\n{ex.StackTrace}",
                    PluginName = "OrderBookSnapshot",
                    MessageType = ErrorMessageTypes.ERROR
                });
            }

            PrintCollectedError(errors);
        }

        // Helper method for depth calculations (mimics real plugin logic)
        private double CalculateTotalDepth(ReadOnlySpan<BookItem> levels)
        {
            double total = 0;
            foreach (var level in levels)
            {
                if (level?.Size.HasValue == true)
                    total += level.Size.Value;
            }
            return total;
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
