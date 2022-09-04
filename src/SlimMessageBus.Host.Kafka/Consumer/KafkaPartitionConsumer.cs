﻿namespace SlimMessageBus.Host.Kafka;

using System.Diagnostics.CodeAnalysis;
using Confluent.Kafka;
using SlimMessageBus.Host.Config;
using SlimMessageBus.Host.Serialization;
using ConsumeResult = Confluent.Kafka.ConsumeResult<Confluent.Kafka.Ignore, byte[]>;

public abstract class KafkaPartitionConsumer : IKafkaPartitionConsumer
{
    private readonly ILogger _logger;
    private readonly IKafkaCommitController _commitController;
    private readonly IMessageSerializer _headerSerializer;
    private IMessageProcessor<ConsumeResult> _messageProcessor;
    private TopicPartitionOffset _lastOffset;
    private TopicPartitionOffset _lastCheckpointOffset;

    protected MessageBusBase MessageBus { get; }
    protected AbstractConsumerSettings[] ConsumerSettings { get; }
    public ICheckpointTrigger CheckpointTrigger { get; set; }
    public string Group { get; }
    public TopicPartition TopicPartition { get; }

    protected KafkaPartitionConsumer(AbstractConsumerSettings[] consumerSettings, string group, TopicPartition topicPartition, IKafkaCommitController commitController, MessageBusBase messageBus, IMessageSerializer headerSerializer)
    {
        _logger = messageBus.LoggerFactory.CreateLogger<KafkaPartitionConsumer>();
        _logger.LogInformation("Creating consumer for Group: {Group}, Topic: {Topic}, Partition: {Partition}", group, topicPartition.Topic, topicPartition.Partition);

        MessageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        ConsumerSettings = consumerSettings ?? throw new ArgumentNullException(nameof(consumerSettings));
        Group = group;
        TopicPartition = topicPartition;

        _headerSerializer = headerSerializer;
        _commitController = commitController;
        _messageProcessor = CreateMessageProcessor();

        // ToDo: Add support for Kafka driven automatic commit
        CheckpointTrigger = CreateCheckpointTrigger();
    }

    private ICheckpointTrigger CreateCheckpointTrigger()
    {
        var f = new CheckpointTriggerFactory(MessageBus.LoggerFactory, (configuredCheckpoints) => $"The checkpoint settings ({nameof(BuilderExtensions.CheckpointAfter)} and {nameof(BuilderExtensions.CheckpointEvery)}) across all the consumers that use the same Topic {TopicPartition.Topic} and Group {Group} must be the same (found settings are: {string.Join(", ", configuredCheckpoints)})");
        return f.Create(ConsumerSettings);
    }

    protected abstract IMessageProcessor<ConsumeResult<Ignore, byte[]>> CreateMessageProcessor();

    #region IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (_messageProcessor != null)
        {
            await _messageProcessor.DisposeAsync();
            _messageProcessor = null;
        }
    }

    #endregion

    #region Implementation of IKafkaTopicPartitionProcessor

    public void OnPartitionAssigned([NotNull] TopicPartition partition)
    {
        _lastCheckpointOffset = null;
        _lastOffset = null;

        CheckpointTrigger?.Reset();
    }

    public async Task OnMessage([NotNull] ConsumeResult message)
    {
        try
        {
            _lastOffset = message.TopicPartitionOffset;

            var messageHeaders = message.ToHeaders(_headerSerializer);
            var (lastException, consumerSettings, response) = await _messageProcessor.ProcessMessage(message, messageHeaders).ConfigureAwait(false);
            if (lastException != null)
            {
                // ToDo: Retry logic
                // The OnMessageFaulted was called at this point by the MessageProcessor.
            }

            if (CheckpointTrigger != null && CheckpointTrigger.Increment())
            {
                Commit(message.TopicPartitionOffset);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Group [{Group}]: Error occured while consuming a message at Topic: {Topic}, Partition: {Partition}, Offset: {Offset}", Group, message.Topic, message.Partition, message.Offset);
            throw;
        }
    }

    public void OnPartitionEndReached(TopicPartitionOffset offset)
    {
        if (CheckpointTrigger != null)
        {
            if (offset != null)
            {
                Commit(offset);
            }
        }
    }

    public void OnPartitionRevoked()
    {
        if (CheckpointTrigger != null)
        {
        }
    }

    public void OnClose()
    {
        if (CheckpointTrigger != null)
        {
            Commit(_lastOffset);
        }
    }

    #endregion

    public void Commit(TopicPartitionOffset offset)
    {
        if (offset != null && (_lastCheckpointOffset == null || offset.Offset > _lastCheckpointOffset.Offset))
        {
            _logger.LogDebug("Group [{Group}]: Commit at Offset: {Offset}, Partition: {Partition}, Topic: {Topic}", Group, offset.Offset, offset.Partition, offset.Topic);

            _lastCheckpointOffset = offset;
            _commitController.Commit(offset);

            CheckpointTrigger?.Reset();
        }
    }
}