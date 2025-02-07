using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Memphis.Client.Constants;
using Memphis.Client.Consumer;
using Memphis.Client.Exception;
using Memphis.Client.Helper;
using Memphis.Client.Models.Request;
using Memphis.Client.Models.Response;
using Memphis.Client.Producer;
using Memphis.Client.Station;
using Memphis.Client.Validators;
using NATS.Client;
using NATS.Client.JetStream;

namespace Memphis.Client
{
    public class MemphisClient : IDisposable
    {
        private readonly Options _brokerConnOptions;
        private readonly IConnection _brokerConnection;
        private readonly IJetStream _jetStreamContext;
        private readonly string _connectionId;
        private readonly string _userName;

        private CancellationTokenSource _cancellationTokenSource;

        // Dictionary key: station (internal)name, value: schema update data for that station
        private readonly ConcurrentDictionary<string, ProducerSchemaUpdateInit> _schemaUpdateDictionary;

        // Dictionary key: station (internal)name, value: subscription for fetching schema updates for station
        private readonly ConcurrentDictionary<string, ISyncSubscription> _subscriptionPerSchema;

        // Dictionary key: station (internal)name, value: number of producer created per that station
        private readonly ConcurrentDictionary<string, int> _producerPerStations;

        private readonly ConcurrentDictionary<ValidatorType, ISchemaValidator> _schemaValidators;

        public MemphisClient(Options brokerConnOptions, IConnection brokerConnection,
            IJetStream jetStreamContext, string connectionId)
        {
            this._brokerConnOptions = brokerConnOptions ?? throw new ArgumentNullException(nameof(brokerConnOptions));
            this._brokerConnection = brokerConnection ?? throw new ArgumentNullException(nameof(brokerConnection));
            this._jetStreamContext = jetStreamContext ?? throw new ArgumentNullException(nameof(jetStreamContext));
            this._connectionId = connectionId ?? throw new ArgumentNullException(nameof(connectionId));
            this._userName = brokerConnOptions.User;

            this._cancellationTokenSource = new CancellationTokenSource();

            this._schemaUpdateDictionary = new ConcurrentDictionary<string, ProducerSchemaUpdateInit>();
            this._subscriptionPerSchema = new ConcurrentDictionary<string, ISyncSubscription>();
            this._producerPerStations = new ConcurrentDictionary<string, int>();

            this._schemaValidators = new ConcurrentDictionary<ValidatorType, ISchemaValidator>();
            this.registerSchemaValidators();
        }


        /// <summary>
        /// Create Producer for station 
        /// </summary>
        /// <param name="stationName">name of station which producer will produce data to</param>
        /// <param name="producerName">name of producer which used to define uniquely</param>
        /// <param name="generateRandomSuffix">feature flag based param used to add randomly generated suffix for producer's name</param>
        /// <returns>An <see cref="MemphisProducer"/> object connected to the station to produce data</returns>
        public async Task<MemphisProducer> CreateProducer(string stationName, string producerName,
            bool generateRandomSuffix = false)
        {
            if (_brokerConnection.IsClosed())
            {
                throw new MemphisConnectionException("Connection is dead");
            }

            if (generateRandomSuffix)
            {
                producerName = $"{producerName}_{MemphisUtil.GetUniqueKey(8)}";
            }

            try
            {
                var createProducerModel = new CreateProducerRequest
                {
                    ProducerName = producerName,
                    StationName = MemphisUtil.GetInternalName(stationName),
                    ConnectionId = _connectionId,
                    ProducerType = "application",
                    RequestVersion = 1,
                    UserName = _userName
                };

                var createProducerModelJson = JsonSerDes.PrepareJsonString<CreateProducerRequest>(createProducerModel);

                byte[] createProducerReqBytes = Encoding.UTF8.GetBytes(createProducerModelJson);

                Msg createProducerResp = await _brokerConnection.RequestAsync(
                    MemphisStations.MEMPHIS_PRODUCER_CREATIONS, createProducerReqBytes);
                string respAsJson = Encoding.UTF8.GetString(createProducerResp.Data);
                var respAsObject =
                    (CreateProducerResponse) JsonSerDes.PrepareObjectFromString<CreateProducerResponse>(respAsJson);

                if (!string.IsNullOrEmpty(respAsObject.Error))
                {
                    throw new MemphisException(respAsObject.Error);
                }

                string internalStationName = MemphisUtil.GetInternalName(stationName);

                await this.listenForSchemaUpdate(internalStationName, respAsObject.SchemaUpdate);

                return new MemphisProducer(this, producerName, stationName);
            }
            catch (System.Exception e)
            {
                throw new MemphisException("Failed to create memphis producer", e);
            }
        }

        /// <summary>
        /// Create Consumer for station 
        /// </summary>
        /// <param name="consumerOptions">options used to customize the behaviour of consumer</param>
        /// <returns>An <see cref="MemphisConsumer"/> object connected to the station from consuming data</returns>
        public async Task<MemphisConsumer> CreateConsumer(ConsumerOptions consumerOptions)
        {
            if (_brokerConnection.IsClosed())
            {
                throw new MemphisConnectionException("Connection is dead");
            }

            if (consumerOptions.GenerateRandomSuffix)
            {
                consumerOptions.ConsumerName = $"{consumerOptions.ConsumerName}_{MemphisUtil.GetUniqueKey(8)}";
            }

            if (string.IsNullOrEmpty(consumerOptions.ConsumerGroup))
            {
                consumerOptions.ConsumerGroup = consumerOptions.ConsumerName;
            }

            try
            {
                var createConsumerModel = new CreateConsumerRequest
                {
                    ConsumerName = consumerOptions.ConsumerName,
                    StationName = consumerOptions.StationName,
                    ConnectionId = _connectionId,
                    ConsumerType = "application",
                    ConsumerGroup = consumerOptions.ConsumerGroup,
                    MaxAckTimeMs = consumerOptions.MaxAckTimeMs,
                    MaxMsgCountForDelivery = consumerOptions.MaxMsdgDeliveries,
                    UserName = _userName
                };

                var createConsumerModelJson = JsonSerDes.PrepareJsonString<CreateConsumerRequest>(createConsumerModel);

                byte[] createConsumerReqBytes = Encoding.UTF8.GetBytes(createConsumerModelJson);

                Msg createConsumerResp = await _brokerConnection.RequestAsync(
                    MemphisStations.MEMPHIS_CONSUMER_CREATIONS, createConsumerReqBytes);
                string errResp = Encoding.UTF8.GetString(createConsumerResp.Data);

                if (!string.IsNullOrEmpty(errResp))
                {
                    throw new MemphisException(errResp);
                }

                return new MemphisConsumer(this, consumerOptions);
            }
            catch (System.Exception e)
            {
                throw new MemphisException("Failed to create memphis producer", e);
            }
        }


        /// <summary>
        /// Create Station 
        /// </summary>
        /// <param name="stationOptions">options used to customize the parameters of station</param>
        /// <returns>An <see cref="MemphisStation"/> object representing the created station</returns>
        public async Task<MemphisStation> CreateStation(StationOptions stationOptions)
        {
            if (_brokerConnection.IsClosed())
            {
                throw new MemphisConnectionException("Connection is dead");
            }

            try
            {
                var createStationModel = new CreateStationRequest()
                {
                    StationName = stationOptions.Name,
                    RetentionType = stationOptions.RetentionType,
                    RetentionValue = stationOptions.RetentionValue,
                    StorageType = stationOptions.StorageType,
                    Replicas = stationOptions.Replicas,
                    IdempotencyWindowsInMs = stationOptions.IdempotencyWindowMs,
                    SchemaName = stationOptions.SchemaName,
                    DlsConfiguration = new DlsConfiguration()
                    {
                        Poison = stationOptions.SendPoisonMessageToDls,
                        SchemaVerse = stationOptions.SendSchemaFailedMessageToDls,
                    },
                    UserName = _userName
                };

                var createStationModelJson = JsonSerDes.PrepareJsonString<CreateStationRequest>(createStationModel);

                byte[] createStationReqBytes = Encoding.UTF8.GetBytes(createStationModelJson);

                Msg createStationResp = await _brokerConnection.RequestAsync(
                    MemphisStations.MEMPHIS_STATION_CREATIONS, createStationReqBytes);
                string errResp = Encoding.UTF8.GetString(createStationResp.Data);

                if (!string.IsNullOrEmpty(errResp))
                {
                    if (errResp.Contains("already exist"))
                    {
                        return new MemphisStation(this, stationOptions.Name);
                    }

                    throw new MemphisException(errResp);
                }

                return new MemphisStation(this, stationOptions.Name);
            }
            catch (System.Exception e)
            {
                throw new MemphisException("Failed to create memphis station", e);
            }
        }

        private async Task listenForSchemaUpdate(string internalStationName, ProducerSchemaUpdateInit schemaUpdateInit)
        {
            var schemaUpdateSubject = MemphisSubjects.MEMPHIS_SCHEMA_UPDATE + internalStationName;

            if (!string.IsNullOrEmpty(schemaUpdateInit.SchemaName))
            {
                _schemaUpdateDictionary.TryAdd(internalStationName, schemaUpdateInit);
            }


            if (_subscriptionPerSchema.TryGetValue(internalStationName, out ISyncSubscription schemaSub))
            {
                _producerPerStations.AddOrUpdate(internalStationName, 1, (key, val) => val + 1);
                return;
            }

            var subscription = _brokerConnection.SubscribeSync(schemaUpdateSubject);

            if (!_subscriptionPerSchema.TryAdd(internalStationName, subscription))
            {
                throw new MemphisException("Unable to add subscription of schema updates for station");
            }

            Task.Run(async () =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    var schemaUpdateMsg = subscription.NextMessage();
                    await processAndStoreSchemaUpdate(internalStationName, schemaUpdateMsg);
                }
            }, _cancellationTokenSource.Token);

            _producerPerStations.AddOrUpdate(internalStationName, 1, (key, val) => val + 1);
        }

        
        /// <summary>
        /// Attach Schema to an existing station
        /// </summary>
        /// <param name="stationName">station name</param>
        /// <param name="schemaName">schema name</param>
        /// <returns>No object or value is returned by this method when it completes.</returns>
        public async Task AttachSchema(string stationName, string schemaName)
        {
            if (string.IsNullOrEmpty(stationName))
            {
                throw new ArgumentException($"{nameof(stationName)} cannot be null or empty");
            }
            
            if (string.IsNullOrEmpty(schemaName))
            {
                throw new ArgumentException($"{nameof(schemaName)} cannot be null or empty");
            }
            
            try
            {
                var attachSchemaRequestModel = new AttachSchemaRequest()
                {
                    SchemaName = schemaName,
                    StationName = stationName,
                    UserName = _userName
                };

                var attachSchemaModelJson = JsonSerDes.PrepareJsonString<AttachSchemaRequest>(attachSchemaRequestModel);

                byte[] attachSchemaReqBytes = Encoding.UTF8.GetBytes(attachSchemaModelJson);

                Msg attachSchemaResp = await _brokerConnection.RequestAsync(
                    MemphisStations.MEMPHIS_SCHEMA_ATTACHMENTS, attachSchemaReqBytes);
                string errResp = Encoding.UTF8.GetString(attachSchemaResp.Data);

                if (!string.IsNullOrEmpty(errResp))
                {
                    throw new MemphisException(errResp);
                } 
            }
            catch (System.Exception e)
            {
                throw new MemphisException("Failed to attach schema to station", e);
            }
            
        }

        
        /// <summary>
        /// DetachSchema Schema from station
        /// </summary>
        /// <param name="stationName">station name</param>
        /// <returns>No object or value is returned by this method when it completes.</returns>
        public async Task DetachSchema(string stationName)
        {
            if (string.IsNullOrEmpty(stationName))
            {
                throw new ArgumentException($"{nameof(stationName)} cannot be null or empty");
            }
            
            try
            {
                var detachSchemaRequestModel = new DetachSchemaRequest()
                {
                    StationName = stationName,
                    UserName  = _userName
                };

                var detachSchemaModelJson = JsonSerDes.PrepareJsonString<DetachSchemaRequest>(detachSchemaRequestModel);

                byte[] detachSchemaReqBytes = Encoding.UTF8.GetBytes(detachSchemaModelJson);

                Msg detachSchemaResp = await _brokerConnection.RequestAsync(
                    MemphisStations.MEMPHIS_SCHEMA_DETACHMENTS, detachSchemaReqBytes);
                string errResp = Encoding.UTF8.GetString(detachSchemaResp.Data);

                if (!string.IsNullOrEmpty(errResp))
                {
                    throw new MemphisException(errResp);
                } 
            }
            catch (System.Exception e)
            {
                throw new MemphisException("Failed to attach schema to station", e);
            }
        }

        
        internal async Task ValidateMessageAsync(byte[] message, string internalStationName, string producerName)
        {
            if (!_schemaUpdateDictionary.TryGetValue(internalStationName,
                out ProducerSchemaUpdateInit schemaUpdateInit))
            {
                return;
            }

            try
            {
                switch (schemaUpdateInit.SchemaType)
                {
                    case ProducerSchemaUpdateInit.SchemaTypes.JSON:
                    {
                        if (_schemaValidators.TryGetValue(ValidatorType.JSON, out ISchemaValidator schemaValidator))
                        {
                            await schemaValidator.ValidateAsync(message, schemaUpdateInit.SchemaName);
                        }

                        break;
                    }
                    case ProducerSchemaUpdateInit.SchemaTypes.GRAPHQL:
                    {
                        if (_schemaValidators.TryGetValue(ValidatorType.GRAPHQL, out ISchemaValidator schemaValidator))
                        {
                            await schemaValidator.ValidateAsync(message, schemaUpdateInit.SchemaName);
                        }

                        break;
                    }
                    case ProducerSchemaUpdateInit.SchemaTypes.PROTOBUF:
                    {
                        throw new NotImplementedException();
                    }
                    default:
                        throw new NotImplementedException($"Schema of type: {schemaUpdateInit.SchemaType} not implemented");
                }
            }
            catch (MemphisSchemaValidationException e)
            {
                await SendNotificationAsync(title: "Schema validation has failed",
                    message: $"Station: {MemphisUtil.GetStationName(internalStationName)}"
                    + $"\nProducer: {producerName}"
                    + $"\nError: {e.Message}",
                    code:  Encoding.UTF8.GetString(message),
                    msgType: "schema_validation_fail_alert");
                throw;
            }
        }

        private async Task processAndStoreSchemaUpdate(string internalStationName, Msg message)
        {
            string respAsJson = Encoding.UTF8.GetString(message.Data);
            var respAsObject =
                (ProducerSchemaUpdate) JsonSerDes.PrepareObjectFromString<ProducerSchemaUpdate>(respAsJson);

            if (!string.IsNullOrEmpty(respAsObject?.Init?.SchemaName))
            {
                if (!this._schemaUpdateDictionary.TryAdd(internalStationName, respAsObject.Init))
                {
                    throw new MemphisException(
                        $"Unable to save schema: {respAsObject.Init.SchemaName} data for station: {internalStationName}");
                }

                switch (respAsObject.Init.SchemaType)
                {
                    case ProducerSchemaUpdateInit.SchemaTypes.JSON:
                    {
                        if (_schemaValidators.TryGetValue(ValidatorType.JSON, out ISchemaValidator schemaValidator))
                        {
                            bool isDone = schemaValidator.ParseAndStore(
                                respAsObject.Init.SchemaName,
                                respAsObject.Init.ActiveVersion?.Content);

                            if (!isDone)
                            {
                                //TODO raise notification regarding unable to parse schema pushed by Memphis
                                throw new InvalidOperationException($"Unable to parse and store " +
                                                                    $"schema: {respAsObject.Init?.SchemaName}, type: {respAsObject.Init?.SchemaType}" +
                                                                    $" in local cache");
                            }
                        }

                        break;
                    }
                    case ProducerSchemaUpdateInit.SchemaTypes.GRAPHQL:
                    {
                        if (_schemaValidators.TryGetValue(ValidatorType.GRAPHQL, out ISchemaValidator schemaValidator))
                        {
                            bool isDone = schemaValidator.ParseAndStore(
                                respAsObject.Init.SchemaName,
                                respAsObject.Init.ActiveVersion?.Content);

                            if (!isDone)
                            {
                                //TODO raise notification regarding unable to parse schema pushed by Memphis
                                throw new InvalidOperationException($"Unable to parse and store " +
                                                                    $"schema: {respAsObject.Init?.SchemaName}, type: {respAsObject.Init?.SchemaType}" +
                                                                    $" in local cache");
                            }
                        }

                        break;
                    }
                    case ProducerSchemaUpdateInit.SchemaTypes.PROTOBUF:
                    {
                        throw new NotImplementedException();
                    }
                }
            }
        }

        public async Task NotifyRemoveProducer(string stationName)
        {
            var internalStationName = MemphisUtil.GetInternalName(stationName);
            if (_producerPerStations.TryGetValue(internalStationName, out int prodCnt))
            {
                if (prodCnt == 0)
                {
                    return;
                }
                
                var prodCntAfterRemove = prodCnt - 1;
                _producerPerStations.TryUpdate(internalStationName, prodCntAfterRemove, prodCnt);

                // Is there any producer for given station ?
                if (prodCntAfterRemove == 0)
                {
                    //unsubscribe for listening schema updates
                    if (_subscriptionPerSchema.TryGetValue(internalStationName, out ISyncSubscription subscription))
                    {
                        await subscription.DrainAsync();
                    }

                    if (_schemaUpdateDictionary.TryRemove(internalStationName,
                        out ProducerSchemaUpdateInit schemaUpdateInit)
                    )
                    {
                        // clean up cache from unused schema data
                        foreach (var schemaValidator in _schemaValidators.Values)
                        {
                            schemaValidator.RemoveSchema(schemaUpdateInit.SchemaName);
                        }
                    }
                }
            }
        }
        
        public void NotifyRemoveConsumer(string stationName)
        {
            return;
        }

        internal async Task SendNotificationAsync(string title, string message, string code, string msgType)
        {
            var notificationModel = new NotificationRequest()
            {
                Title = title,
                Message = message,
                Code = code,
                Type = msgType
            };

            var notificationModelJson = JsonSerDes.PrepareJsonString<NotificationRequest>(notificationModel);

            byte[] notificationReqBytes = Encoding.UTF8.GetBytes(notificationModelJson);

            _ = await _brokerConnection.RequestAsync(MemphisStations.MEMPHIS_NOTIFICATIONS, notificationReqBytes);
        }
        
        
        private void registerSchemaValidators()
        {
            if (!_schemaValidators.TryAdd(ValidatorType.GRAPHQL, new GraphqlValidator()))
            {
                throw new InvalidOperationException($"Unable to register schema validator: {nameof(GraphqlValidator)}");
            }

            if (!_schemaValidators.TryAdd(ValidatorType.JSON, new JsonValidator()))
            {
                throw new InvalidOperationException($"Unable to register schema validator: {nameof(JsonValidator)}");
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();

            _brokerConnection.Dispose();
        }

        internal IConnection BrokerConnection
        {
            get { return _brokerConnection; }
        }

        internal IJetStream JetStreamConnection
        {
            get { return _jetStreamContext; }
        }

        internal string ConnectionId
        {
            get { return _connectionId; }
        }
    }
}