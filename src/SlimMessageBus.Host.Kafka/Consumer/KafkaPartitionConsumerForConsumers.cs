namespace SlimMessageBus.Host.Kafka;

using System.Diagnostics.CodeAnalysis;

using ConsumeResult = ConsumeResult<Ignore, byte[]>;

/// <summary>
/// Processor for regular consumers. 
/// See also <see cref="IKafkaPartitionConsumer"/>.
/// </summary>
public class KafkaPartitionConsumerForConsumers : KafkaPartitionConsumer
{
    public KafkaPartitionConsumerForConsumers(ConsumerSettings[] consumerSettings, string group, TopicPartition topicPartition, IKafkaCommitController commitController, [NotNull] MessageBusBase messageBus, [NotNull] IMessageSerializer headerSerializer)
        : base(consumerSettings, group, topicPartition, commitController, messageBus, headerSerializer)
    {
    }

    protected override IMessageProcessor<ConsumeResult<Ignore, byte[]>> CreateMessageProcessor()
        => new ConsumerInstanceMessageProcessor<ConsumeResult>(ConsumerSettings, MessageBus, messageProvider: GetMessageFromTransportMessage, path: TopicPartition.Topic, (m, ctx) => ctx.SetTransportMessage(m));


    private object GetMessageFromTransportMessage(Type messageType, ConsumeResult transportMessage)
        => MessageBus.Serializer.Deserialize(messageType, transportMessage.Message.Value);
}