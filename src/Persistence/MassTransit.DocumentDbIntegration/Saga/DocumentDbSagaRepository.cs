﻿namespace MassTransit.DocumentDbIntegration.Saga
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;
    using Context;
    using GreenPipes;
    using GreenPipes.Internals.Extensions;
    using MassTransit.Context;
    using MassTransit.Saga;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Client;
    using Newtonsoft.Json;
    using Pipeline;
    using Util;


    public class DocumentDbSagaRepository<TSaga> :
        ISagaRepository<TSaga>,
        IQuerySagaRepository<TSaga>
        where TSaga : class, IVersionedSaga
    {
        public const string DefaultCollectionName = "sagas";

        readonly IDocumentClient _client;
        readonly string _collectionName;
        readonly string _databaseName;
        readonly IDocumentDbSagaConsumeContextFactory _documentDbSagaConsumeContextFactory;
        readonly FeedOptions _feedOptions;
        readonly JsonSerializerSettings _jsonSerializerSettings;
        readonly RequestOptions _requestOptions;

        public DocumentDbSagaRepository(IDocumentClient client, string databaseName, string collectionName)
            : this(client, databaseName, collectionName, new DocumentDbSagaConsumeContextFactory(), null, null)
        {
        }

        public DocumentDbSagaRepository(IDocumentClient client, string databaseName, IDocumentDbSagaConsumeContextFactory documentDbSagaConsumeContextFactory)
            : this(client, databaseName, DefaultCollectionName, documentDbSagaConsumeContextFactory, null, null)
        {
        }

        public DocumentDbSagaRepository(IDocumentClient client, string databaseName)
            : this(client, databaseName, DefaultCollectionName, new DocumentDbSagaConsumeContextFactory(), null, null)
        {
        }

        public DocumentDbSagaRepository(IDocumentClient client, string databaseName, JsonSerializerSettings jsonSerializerSettings)
            : this(client, databaseName, DefaultCollectionName, new DocumentDbSagaConsumeContextFactory(), jsonSerializerSettings)
        {
        }

        public DocumentDbSagaRepository(IDocumentClient client,
            string databaseName,
            string collectionName,
            IDocumentDbSagaConsumeContextFactory documentDbSagaConsumeContextFactory,
            JsonSerializerSettings jsonSerializerSettings)
            : this(client, databaseName, collectionName, documentDbSagaConsumeContextFactory, null, null)
        {
            if (jsonSerializerSettings != null)
            {
                _jsonSerializerSettings = jsonSerializerSettings;
                _requestOptions.JsonSerializerSettings = jsonSerializerSettings;
                _feedOptions.JsonSerializerSettings = jsonSerializerSettings;
            }
        }

        public DocumentDbSagaRepository(IDocumentClient client,
            string databaseName,
            string collectionName,
            IDocumentDbSagaConsumeContextFactory documentDbSagaConsumeContextFactory,
            FeedOptions feedOptions,
            RequestOptions requestOptions)
        {
            if (string.IsNullOrWhiteSpace(collectionName))
                throw new ArgumentNullException(nameof(collectionName));

            if (collectionName.Length > 120)
                throw new ArgumentException("Collection names must be no longer than 120 characters", nameof(collectionName));

            _client = client;

            _databaseName = databaseName;
            _collectionName = collectionName;
            _documentDbSagaConsumeContextFactory =
                documentDbSagaConsumeContextFactory ?? throw new ArgumentNullException(nameof(documentDbSagaConsumeContextFactory));

            _feedOptions = feedOptions ?? new FeedOptions();
            _requestOptions = requestOptions ?? new RequestOptions();
        }

        public async Task<IEnumerable<Guid>> Find(ISagaQuery<TSaga> query)
        {
            // This will not work for Document Db because the .Where needs to look for [JsonProperty("id")], and if you pass in CorrelationId property, the ISaga doesn't have that property.
            IEnumerable<TSaga> sagas = await _client
                .CreateDocumentQuery<TSaga>(UriFactory.CreateDocumentCollectionUri(_databaseName, _collectionName), _feedOptions)
                .Where(query.FilterExpression)
                .QueryAsync()
                .ConfigureAwait(false);

            return sagas.Select(x => x.CorrelationId);
        }

        public void Probe(ProbeContext context)
        {
            var scope = context.CreateScope("sagaRepository");

            scope.Set(new
            {
                Persistence = "documentdb",
                SagaType = TypeMetadataCache<TSaga>.ShortName,
                Properties = TypeCache<TSaga>.ReadWritePropertyCache.Select(x => x.Property.Name).ToArray()
            });
        }

        public async Task Send<T>(ConsumeContext<T> context, ISagaPolicy<TSaga, T> policy, IPipe<SagaConsumeContext<TSaga, T>> next)
            where T : class
        {
            if (!context.CorrelationId.HasValue)
                throw new SagaException("The CorrelationId was not specified", typeof(TSaga), typeof(T));

            TSaga instance = null;

            if (policy.PreInsertInstance(context, out instance))
                instance = await PreInsertSagaInstance(context, instance).ConfigureAwait(false);

            if (instance == null)
            {
                try
                {
                    ResourceResponse<Document> response = await _client
                        .ReadDocumentAsync(UriFactory.CreateDocumentUri(_databaseName, _collectionName, context.CorrelationId.ToString()), _requestOptions)
                        .ConfigureAwait(false);

                    instance = JsonConvert.DeserializeObject<TSaga>(response.Resource.ToString(), _jsonSerializerSettings);
                }
                catch (DocumentClientException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                    // Couldn't find the document, swallowing exception
                }
            }

            if (instance == null)
            {
                var missingSagaPipe =
                    new MissingPipe<TSaga, T>(_client, _databaseName, _collectionName, next, _documentDbSagaConsumeContextFactory, _jsonSerializerSettings);

                await policy.Missing(context, missingSagaPipe).ConfigureAwait(false);
            }
            else
                await SendToInstance(context, policy, next, instance).ConfigureAwait(false);
        }

        public async Task SendQuery<T>(SagaQueryConsumeContext<TSaga, T> context, ISagaPolicy<TSaga, T> policy, IPipe<SagaConsumeContext<TSaga, T>> next)
            where T : class
        {
            try
            {
                List<TSaga> sagaInstances = (await _client
                    .CreateDocumentQuery<TSaga>(UriFactory.CreateDocumentCollectionUri(_databaseName, _collectionName), _feedOptions)
                    .Where(context.Query.FilterExpression).QueryAsync().ConfigureAwait(false)).ToList();

                if (!sagaInstances.Any())
                {
                    var missingPipe = new MissingPipe<TSaga, T>(_client, _databaseName, _collectionName, next, _documentDbSagaConsumeContextFactory,
                        _jsonSerializerSettings);

                    await policy.Missing(context, missingPipe).ConfigureAwait(false);
                }
                else
                {
                    foreach (var instance in sagaInstances)
                        await SendToInstance(context, policy, next, instance).ConfigureAwait(false);
                }
            }
            catch (SagaException sex)
            {
                LogContext.Error?.Log(sex, "SAGA:{SagaType} Exception {MessageType}", TypeMetadataCache<TSaga>.ShortName, TypeMetadataCache<T>.ShortName);

                throw;
            }
            catch (Exception ex)
            {
                LogContext.Error?.Log(ex, "SAGA:{SagaType} Exception {MessageType}", TypeMetadataCache<TSaga>.ShortName, TypeMetadataCache<T>.ShortName);

                throw new SagaException(ex.Message, typeof(TSaga), typeof(T), Guid.Empty, ex);
            }
        }

        async Task<TSaga> PreInsertSagaInstance<T>(ConsumeContext<T> context, TSaga instance)
            where T : class
        {
            try
            {
                ResourceResponse<Document> response =
                    await _client.CreateDocumentAsync(UriFactory.CreateDocumentCollectionUri(_databaseName, _collectionName), instance, _requestOptions, true)
                        .ConfigureAwait(false);

                LogContext.Debug?.Log("SAGA:{SagaType}:{CorrelationId} Insert {MessageType}", TypeMetadataCache<TSaga>.ShortName,
                    instance.CorrelationId, TypeMetadataCache<T>.ShortName);

                return JsonConvert.DeserializeObject<TSaga>(response.Resource.ToString(), _jsonSerializerSettings);
            }
            catch (Exception ex)
            {
                LogContext.Debug?.Log(ex, "SAGA:{SagaType}:{CorrelationId} Dupe {MessageType}", TypeMetadataCache<TSaga>.ShortName,
                    instance.CorrelationId, TypeMetadataCache<T>.ShortName);
            }

            return null;
        }

        async Task SendToInstance<T>(ConsumeContext<T> context, ISagaPolicy<TSaga, T> policy, IPipe<SagaConsumeContext<TSaga, T>> next, TSaga instance)
            where T : class
        {
            try
            {
                LogContext.Debug?.Log("SAGA:{SagaType}:{CorrelationId} Used {MessageType}", TypeMetadataCache<TSaga>.ShortName,
                    instance.CorrelationId, TypeMetadataCache<T>.ShortName);

                SagaConsumeContext<TSaga, T> sagaConsumeContext =
                    _documentDbSagaConsumeContextFactory.Create(_client, _databaseName, _collectionName, context, instance, true, _requestOptions);

                await policy.Existing(sagaConsumeContext, next).ConfigureAwait(false);

                if (!sagaConsumeContext.IsCompleted)
                    await UpdateDocumentDbSaga(context, instance).ConfigureAwait(false);
            }
            catch (SagaException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SagaException(ex.Message, typeof(TSaga), typeof(T), instance.CorrelationId, ex);
            }
        }

        async Task UpdateDocumentDbSaga(PipeContext context, TSaga instance)
        {
            // DocumentDb Optimistic Concurrency https://codeopinion.com/documentdb-optimistic-concurrency/
            var ac = new AccessCondition
            {
                Condition = instance.ETag,
                Type = AccessConditionType.IfMatch
            };

            try
            {
                _requestOptions.AccessCondition = ac;
                await _client.ReplaceDocumentAsync(UriFactory.CreateDocumentUri(_databaseName, _collectionName, instance.CorrelationId.ToString()), instance,
                    _requestOptions).ConfigureAwait(false);
            }
            catch (DocumentClientException e) when (e.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new DocumentDbConcurrencyException("Unable to update saga. It may not have been found or may have been updated by another process.");
            }
            finally
            {
                _requestOptions.AccessCondition = null; // Unassign
            }
        }
    }
}