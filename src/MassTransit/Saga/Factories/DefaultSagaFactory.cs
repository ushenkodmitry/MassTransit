namespace MassTransit.Saga.Factories
{
    using System.Threading.Tasks;
    using Context;
    using GreenPipes;
    using Metadata;
    using Util;


    /// <summary>
    /// Creates a saga instance using the default factory method
    /// </summary>
    /// <typeparam name="TSaga"></typeparam>
    /// <typeparam name="TMessage"></typeparam>
    public class DefaultSagaFactory<TSaga, TMessage> :
        ISagaFactory<TSaga, TMessage>
        where TSaga : class, ISaga
        where TMessage : class
    {
        public TSaga Create(ConsumeContext<TMessage> context)
        {
            if (!context.CorrelationId.HasValue)
                throw new SagaException("The correlationId was not present and the saga could not be created", typeof(TSaga), typeof(TMessage));

            return SagaMetadataCache<TSaga>.FactoryMethod(context.CorrelationId.Value);
        }

        public Task Send(ConsumeContext<TMessage> context, IPipe<SagaConsumeContext<TSaga, TMessage>> next)
        {
            if (!context.CorrelationId.HasValue)
                throw new SagaException("The correlationId was not present and the saga could not be created", typeof(TSaga), typeof(TMessage));

            TSaga instance = SagaMetadataCache<TSaga>.FactoryMethod(context.CorrelationId.Value);

            LogContext.Debug?.Log("SAGA:{SagaType}:{CorrelationId} Created {MessageType}", TypeMetadataCache<TSaga>.ShortName, instance.CorrelationId,
                TypeMetadataCache<TMessage>.ShortName);

            var proxy = new NewSagaConsumeContext<TSaga, TMessage>(context, instance);

            return next.Send(proxy);
        }
    }
}