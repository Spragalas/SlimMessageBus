﻿namespace SlimMessageBus.Host.Test;

using Microsoft.Extensions.DependencyInjection;

using SlimMessageBus.Host.Interceptor;

public class ConsumerInstanceMessageProcessorTest
{
    private readonly MessageBusMock _busMock;
    private readonly Mock<Func<Type, byte[], object>> _messageProviderMock;

    public ConsumerInstanceMessageProcessorTest()
    {
        _busMock = new MessageBusMock();
        _messageProviderMock = new Mock<Func<Type, byte[], object>>();
    }

    [Fact]
    public async Task When_RequestExpired_Then_HandlerNeverCalled()
    {
        // arrange
        var consumerSettings = new HandlerBuilder<SomeRequest, SomeResponse>(new MessageBusSettings()).Topic(null).WithHandler<IRequestHandler<SomeRequest, SomeResponse>>().ConsumerSettings;

        var transportMessage = Array.Empty<byte>();

        var request = new SomeRequest();
        var headers = new Dictionary<string, object>();
        headers.SetHeader(ReqRespMessageHeaders.Expires, _busMock.CurrentTime.AddSeconds(-10));
        headers.SetHeader(ReqRespMessageHeaders.RequestId, "request-id");

        object MessageProvider(Type messageType, byte[] payload) => request;

        var p = new ConsumerInstanceMessageProcessor<byte[]>(new[] { consumerSettings }, _busMock.Bus, MessageProvider, "path");

        _busMock.SerializerMock.Setup(x => x.Deserialize(typeof(SomeRequest), It.IsAny<byte[]>())).Returns(request);

        // act
        await p.ProcessMessage(transportMessage, headers, default);

        // assert
        _busMock.HandlerMock.Verify(x => x.OnHandle(It.IsAny<SomeRequest>()), Times.Never); // the handler should not be called
        _busMock.HandlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task When_RequestFails_Then_ErrorResponseIsSent()
    {
        // arrange
        var topic = "topic";
        var consumerSettings = new HandlerBuilder<SomeRequest, SomeResponse>(new MessageBusSettings()).Topic(topic).WithHandler<IRequestHandler<SomeRequest, SomeResponse>>().Instances(1).ConsumerSettings;

        var replyTo = "reply-topic";
        var requestId = "request-id";

        var transportMessage = Array.Empty<byte>();

        var request = new SomeRequest();
        var headers = new Dictionary<string, object>();
        headers.SetHeader(ReqRespMessageHeaders.RequestId, requestId);
        headers.SetHeader(ReqRespMessageHeaders.ReplyTo, replyTo);
        object MessageProvider(Type messageType, byte[] payload) => request;

        var p = new ConsumerInstanceMessageProcessor<byte[]>(new[] { consumerSettings }, _busMock.Bus, MessageProvider, topic);

        _busMock.SerializerMock.Setup(x => x.Deserialize(typeof(SomeRequest), It.IsAny<byte[]>())).Returns(request);

        var ex = new Exception("Something went bad");
        _busMock.HandlerMock.Setup(x => x.OnHandle(request)).Returns(Task.FromException<SomeResponse>(ex));

        // act
        var (exception, exceptionConsumerSettings, response, requestReturned) = await p.ProcessMessage(transportMessage, headers, default);

        // assert
        requestReturned.Should().BeSameAs(request);
        _busMock.HandlerMock.Verify(x => x.OnHandle(request), Times.Once); // handler called once
        _busMock.HandlerMock.VerifyNoOtherCalls();

        _busMock.BusMock.Verify(
            x => x.ProduceResponse(
                request,
                It.IsAny<IReadOnlyDictionary<string, object>>(),
                It.IsAny<SomeResponse>(),
                It.Is<IDictionary<string, object>>(m => (string)m[ReqRespMessageHeaders.RequestId] == requestId),
                It.IsAny<ConsumerSettings>()));

        exception.Should().BeNull();
    }

    [Fact]
    public async Task When_MessageFails_Then_ExceptionReturned()
    {
        // arrange
        var topic = "topic";
        var consumerSettings = new ConsumerBuilder<SomeMessage>(new MessageBusSettings()).Topic(topic).WithConsumer<IConsumer<SomeMessage>>().Instances(1).ConsumerSettings;

        var transportMessage = Array.Empty<byte>();

        var message = new SomeMessage();
        var messageHeaders = new Dictionary<string, object>();
        _messageProviderMock.Setup(x => x(message.GetType(), It.IsAny<byte[]>())).Returns(message);

        var ex = new Exception("Something went bad");
        _busMock.ConsumerMock.Setup(x => x.OnHandle(message)).ThrowsAsync(ex);

        var p = new ConsumerInstanceMessageProcessor<byte[]>(new[] { consumerSettings }, _busMock.Bus, _messageProviderMock.Object, topic);

        // act
        var (exception, exceptionConsumerSettings, response, messageReturned) = await p.ProcessMessage(transportMessage, messageHeaders, default);

        // assert
        messageReturned.Should().BeSameAs(message);
        _busMock.ConsumerMock.Verify(x => x.OnHandle(message), Times.Once); // handler called once
        _busMock.ConsumerMock.VerifyNoOtherCalls();

        exception.Should().BeSameAs(exception);
        exceptionConsumerSettings.Should().BeSameAs(consumerSettings);
    }

    [Fact]
    public async Task When_MessageArrives_Then_MessageHandlerIsCalled()
    {
        // arrange
        var message = new SomeMessage();
        var topic = "topic1";

        var consumerSettings = new ConsumerBuilder<SomeMessage>(_busMock.Bus.Settings).Topic(topic).WithConsumer<IConsumer<SomeMessage>>().ConsumerSettings;

        var transportMessage = Array.Empty<byte>();

        _messageProviderMock.Setup(x => x(message.GetType(), It.IsAny<byte[]>())).Returns(message);
        _busMock.ConsumerMock.Setup(x => x.OnHandle(message)).Returns(Task.CompletedTask);

        var p = new ConsumerInstanceMessageProcessor<byte[]>(new[] { consumerSettings }, _busMock.Bus, _messageProviderMock.Object, topic);

        // act
        var (_, _, _, messageReturned) = await p.ProcessMessage(transportMessage, new Dictionary<string, object>(), default);

        // assert
        messageReturned.Should().BeSameAs(message);
        _busMock.ConsumerMock.Verify(x => x.OnHandle(message), Times.Once); // handler called once
        _busMock.ConsumerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task When_MessageArrives_Then_ConsumerInterceptorIsCalled()
    {
        // arrange
        var message = new SomeMessage();
        var topic = "topic1";

        var messageConsumerInterceptor = new Mock<IConsumerInterceptor<SomeMessage>>();
        messageConsumerInterceptor
            .Setup(x => x.OnHandle(message, It.IsAny<Func<Task<object>>>(), It.IsAny<IConsumerContext>()))
            .Returns((SomeMessage message, Func<Task<object>> next, IConsumerContext context) => next());

        _busMock.DependencyResolverMock
            .Setup(x => x.GetService(typeof(IEnumerable<IConsumerInterceptor<SomeMessage>>)))
            .Returns(new[] { messageConsumerInterceptor.Object });

        var consumerSettings = new ConsumerBuilder<SomeMessage>(_busMock.Bus.Settings).Topic(topic).WithConsumer<IConsumer<SomeMessage>>().ConsumerSettings;

        _messageProviderMock.Setup(x => x(message.GetType(), It.IsAny<byte[]>())).Returns(message);
        _busMock.ConsumerMock.Setup(x => x.OnHandle(message)).Returns(Task.CompletedTask);

        var p = new ConsumerInstanceMessageProcessor<byte[]>(new[] { consumerSettings }, _busMock.Bus, _messageProviderMock.Object, topic);

        // act
        var result = await p.ProcessMessage(Array.Empty<byte>(), new Dictionary<string, object>(), default);

        // assert
        result.Exception.Should().BeNull();
        result.Response.Should().BeNull();

        _busMock.ConsumerMock.Verify(x => x.OnHandle(message), Times.Once); // handler called once
        _busMock.ConsumerMock.VerifyNoOtherCalls();

        messageConsumerInterceptor.Verify(x => x.OnHandle(message, It.IsAny<Func<Task<object>>>(), It.IsAny<IConsumerContext>()), Times.Once);
        messageConsumerInterceptor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task When_RequestArrives_Given_Request_Then_RequestHandlerInterceptorIsCalled_Given_SendResponsesIsFalse()
    {
        // arrange
        var request = new SomeRequest();
        var requestPayload = Array.Empty<byte>();
        var response = new SomeResponse();
        var topic = "topic1";

        var handlerMock = new Mock<IRequestHandler<SomeRequest, SomeResponse>>();
        handlerMock
            .Setup(x => x.OnHandle(request))
            .Returns(Task.FromResult(response));

        var requestHandlerInterceptor = new Mock<IRequestHandlerInterceptor<SomeRequest, SomeResponse>>();
        requestHandlerInterceptor
            .Setup(x => x.OnHandle(request, It.IsAny<Func<Task<SomeResponse>>>(), It.IsAny<IConsumerContext>()))
            .Returns((SomeRequest message, Func<Task<SomeResponse>> next, IConsumerContext context) => next?.Invoke());

        _busMock.DependencyResolverMock
            .Setup(x => x.GetService(typeof(IRequestHandler<SomeRequest, SomeResponse>)))
            .Returns(handlerMock.Object);

        _busMock.DependencyResolverMock
            .Setup(x => x.GetService(typeof(IEnumerable<IRequestHandlerInterceptor<SomeRequest, SomeResponse>>)))
            .Returns(new[] { requestHandlerInterceptor.Object });

        var consumerSettings = new HandlerBuilder<SomeRequest, SomeResponse>(_busMock.Bus.Settings).Topic(topic).WithHandler<IRequestHandler<SomeRequest, SomeResponse>>().ConsumerSettings;

        _messageProviderMock.Setup(x => x(request.GetType(), requestPayload)).Returns(request);

        var p = new ConsumerInstanceMessageProcessor<byte[]>(new[] { consumerSettings }, _busMock.Bus, _messageProviderMock.Object, topic, sendResponses: false);

        // act
        var result = await p.ProcessMessage(requestPayload, new Dictionary<string, object>(), default);

        // assert
        result.Exception.Should().BeNull();
        result.Response.Should().BeSameAs(response);

        requestHandlerInterceptor.Verify(x => x.OnHandle(request, It.IsAny<Func<Task<SomeResponse>>>(), It.IsAny<IConsumerContext>()), Times.Once);
        requestHandlerInterceptor.VerifyNoOtherCalls();

        handlerMock.Verify(x => x.OnHandle(request), Times.Once); // handler called once
        handlerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task When_RequestArrives_Given_RequestWithoutResponse_Then_RequestHandlerInterceptorIsCalled_Given_SendResponsesIsFalse()
    {
        // arrange
        var request = new SomeRequestWithoutResponse();
        var requestPayload = Array.Empty<byte>();
        var topic = "topic1";

        var handlerMock = new Mock<IRequestHandler<SomeRequestWithoutResponse>>();
        handlerMock
            .Setup(x => x.OnHandle(request))
            .Returns(Task.CompletedTask);

        var requestHandlerInterceptor = new Mock<IRequestHandlerInterceptor<SomeRequestWithoutResponse, Void>>();
        requestHandlerInterceptor
            .Setup(x => x.OnHandle(request, It.IsAny<Func<Task<Void>>>(), It.IsAny<IConsumerContext>()))
            .Returns((SomeRequestWithoutResponse message, Func<Task<Void>> next, IConsumerContext context) => next?.Invoke());

        _busMock.DependencyResolverMock
            .Setup(x => x.GetService(typeof(IRequestHandler<SomeRequestWithoutResponse>)))
            .Returns(handlerMock.Object);

        _busMock.DependencyResolverMock
            .Setup(x => x.GetService(typeof(IEnumerable<IRequestHandlerInterceptor<SomeRequestWithoutResponse, Void>>)))
            .Returns(new[] { requestHandlerInterceptor.Object });

        var consumerSettings = new HandlerBuilder<SomeRequestWithoutResponse>(_busMock.Bus.Settings).Topic(topic).WithHandler<IRequestHandler<SomeRequestWithoutResponse>>().ConsumerSettings;

        _messageProviderMock.Setup(x => x(request.GetType(), requestPayload)).Returns(request);

        var p = new ConsumerInstanceMessageProcessor<byte[]>(new[] { consumerSettings }, _busMock.Bus, _messageProviderMock.Object, topic, sendResponses: false);

        // act
        var result = await p.ProcessMessage(requestPayload, new Dictionary<string, object>(), default);

        // assert
        result.Exception.Should().BeNull();
        result.Response.Should().BeNull();

        requestHandlerInterceptor.Verify(x => x.OnHandle(request, It.IsAny<Func<Task<Void>>>(), It.IsAny<IConsumerContext>()), Times.Once);
        requestHandlerInterceptor.VerifyNoOtherCalls();

        handlerMock.Verify(x => x.OnHandle(request), Times.Once); // handler called once
        handlerMock.VerifyNoOtherCalls();
    }

    public class SomeMessageConsumerWithContext : IConsumer<SomeMessage>, IConsumerWithContext
    {
        public virtual IConsumerContext Context { get; set; }

        public virtual Task OnHandle(SomeMessage message) => Task.CompletedTask;
    }

    [Fact]
    public async Task When_MessageArrives_Given_ConsumerWithContext_Then_ConsumerContextIsSet()
    {
        // arrange
        var message = new SomeMessage();
        var messageBytes = Array.Empty<byte>();
        var topic = "topic1";
        var headers = new Dictionary<string, object>();
        IConsumerContext context = null;
        CancellationToken cancellationToken = default;

        var consumerMock = new Mock<SomeMessageConsumerWithContext>();
        consumerMock.Setup(x => x.OnHandle(message)).Returns(Task.CompletedTask);
        consumerMock.SetupSet(x => x.Context = It.IsAny<IConsumerContext>())
            .Callback<IConsumerContext>(p => context = p)
            .Verifiable();

        _busMock.DependencyResolverMock.Setup(x => x.GetService(typeof(SomeMessageConsumerWithContext))).Returns(consumerMock.Object);

        var consumerSettings = new ConsumerBuilder<SomeMessage>(_busMock.Bus.Settings).Topic(topic).WithConsumer<SomeMessageConsumerWithContext>().ConsumerSettings;

        _messageProviderMock.Setup(x => x(message.GetType(), messageBytes)).Returns(message);

        var p = new ConsumerInstanceMessageProcessor<byte[]>(new[] { consumerSettings }, _busMock.Bus, _messageProviderMock.Object, topic);

        // act
        await p.ProcessMessage(messageBytes, headers, cancellationToken);

        // assert
        consumerMock.Verify(x => x.OnHandle(message), Times.Once); // handler called once
        consumerMock.VerifySet(x => x.Context = It.IsAny<IConsumerContext>());
        consumerMock.VerifyNoOtherCalls();

        context.Should().NotBeNull();
        context.Path.Should().Be(topic);
        context.CancellationToken.Should().Be(cancellationToken);
        context.Headers.Should().BeSameAs(headers);
        context.Consumer.Should().BeSameAs(consumerMock.Object);
    }

    [Fact]
    public async Task When_MessageArrives_And_MessageScopeEnabled_Then_ScopeIsCreated_InstanceIsRetrivedFromScope_ConsumeMethodExecuted()
    {
        // arrange
        var topic = "topic1";

        var consumerSettings = new ConsumerBuilder<SomeMessage>(_busMock.Bus.Settings).Topic(topic).WithConsumer<IConsumer<SomeMessage>>().Instances(1).PerMessageScopeEnabled(true).ConsumerSettings;
        _busMock.BusMock.Setup(x => x.IsMessageScopeEnabled(consumerSettings)).Returns(true);

        var message = new SomeMessage();

        _messageProviderMock.Setup(x => x(message.GetType(), It.IsAny<byte[]>())).Returns(message);
        _busMock.ConsumerMock.Setup(x => x.OnHandle(message)).Returns(Task.CompletedTask);

        var p = new ConsumerInstanceMessageProcessor<byte[]>(new[] { consumerSettings }, _busMock.Bus, _messageProviderMock.Object, topic);

        Mock<IServiceProvider> childScopeMock = null;

        _busMock.OnChildDependencyResolverCreated = (_, mock) =>
        {
            childScopeMock = mock;
        };

        // act
        await p.ProcessMessage(Array.Empty<byte>(), new Dictionary<string, object>(), default);

        // assert
        _busMock.ConsumerMock.Verify(x => x.OnHandle(message), Times.Once); // handler called once
        _busMock.DependencyResolverMock.Verify(x => x.GetService(typeof(IServiceScopeFactory)), Times.Once);
        _busMock.ChildDependencyResolverMocks.Count.Should().Be(0); // it has been disposed
        childScopeMock.Should().NotBeNull();
        childScopeMock.Verify(x => x.GetService(typeof(IConsumer<SomeMessage>)), Times.Once);
    }

    public static IEnumerable<object[]> Data => new List<object[]>
    {
        new object[] { new SomeMessage(), false, false },
        new object[] { new SomeDerivedMessage(), false, false },
        new object[] { new SomeRequest(), false, false },
        new object[] { new SomeDerived2Message(), false, false },
        new object[] { new object(), true, true, },
        new object[] { new object(), true, false },
    };

    [Theory]
    [MemberData(nameof(Data))]
    public async Task Given_SeveralConsumersOnSameTopic_When_MessageArrives_Then_MatchingConsumerExecuted(object message, bool isUndeclaredMessageType, bool shouldFailOnUndeclaredMessageType)
    {
        // arrange
        var topic = "topic";

        var consumerSettingsForSomeMessage = new ConsumerBuilder<SomeMessage>(_busMock.Bus.Settings)
            .Topic(topic)
            .WithConsumer<IConsumer<SomeMessage>>()
            .WithConsumer<IConsumer<SomeDerivedMessage>, SomeDerivedMessage>()
            .WhenUndeclaredMessageTypeArrives(opts =>
            {
                opts.Fail = shouldFailOnUndeclaredMessageType;
            })
            .ConsumerSettings;

        var consumerSettingsForSomeMessageInterface = new ConsumerBuilder<ISomeMessageMarkerInterface>(_busMock.Bus.Settings)
            .Topic(topic)
            .WithConsumer<IConsumer<ISomeMessageMarkerInterface>>()
            .ConsumerSettings;

        var consumerSettingsForSomeRequest = new HandlerBuilder<SomeRequest, SomeResponse>(_busMock.Bus.Settings)
            .Topic(topic)
            .WithHandler<IRequestHandler<SomeRequest, SomeResponse>>()
            .ConsumerSettings;

        var messageWithHeaderProviderMock = new Mock<Func<Type, byte[], object>>();

        var mesageHeaders = new Dictionary<string, object>
        {
            [MessageHeaders.MessageType] = _busMock.Bus.MessageTypeResolver.ToName(message.GetType())
        };

        var p = new ConsumerInstanceMessageProcessor<byte[]>(new[] { consumerSettingsForSomeMessage, consumerSettingsForSomeRequest, consumerSettingsForSomeMessageInterface }, _busMock.Bus, messageWithHeaderProviderMock.Object, topic);

        var transportMessage = new byte[] { 255 };

        _busMock.SerializerMock.Setup(x => x.Deserialize(message.GetType(), transportMessage)).Returns(message);
        messageWithHeaderProviderMock.Setup(x => x(message.GetType(), transportMessage)).Returns(message);

        var someMessageConsumerMock = new Mock<IConsumer<SomeMessage>>();
        var someMessageInterfaceConsumerMock = new Mock<IConsumer<ISomeMessageMarkerInterface>>();
        var someDerivedMessageConsumerMock = new Mock<IConsumer<SomeDerivedMessage>>();
        var someRequestMessageHandlerMock = new Mock<IRequestHandler<SomeRequest, SomeResponse>>();

        _busMock.DependencyResolverMock.Setup(x => x.GetService(typeof(IConsumer<SomeMessage>))).Returns(someMessageConsumerMock.Object);
        _busMock.DependencyResolverMock.Setup(x => x.GetService(typeof(IConsumer<ISomeMessageMarkerInterface>))).Returns(someMessageInterfaceConsumerMock.Object);
        _busMock.DependencyResolverMock.Setup(x => x.GetService(typeof(IConsumer<SomeDerivedMessage>))).Returns(someDerivedMessageConsumerMock.Object);
        _busMock.DependencyResolverMock.Setup(x => x.GetService(typeof(IRequestHandler<SomeRequest, SomeResponse>))).Returns(someRequestMessageHandlerMock.Object);

        someMessageConsumerMock.Setup(x => x.OnHandle(It.IsAny<SomeMessage>())).Returns(Task.CompletedTask);
        someMessageInterfaceConsumerMock.Setup(x => x.OnHandle(It.IsAny<ISomeMessageMarkerInterface>())).Returns(Task.CompletedTask);
        someDerivedMessageConsumerMock.Setup(x => x.OnHandle(It.IsAny<SomeDerivedMessage>())).Returns(Task.CompletedTask);
        someRequestMessageHandlerMock.Setup(x => x.OnHandle(It.IsAny<SomeRequest>())).Returns(Task.FromResult(new SomeResponse()));

        // act
        var (exception, consumerSettings, response, _) = await p.ProcessMessage(transportMessage, mesageHeaders, default);

        // assert
        consumerSettings.Should().BeNull();
        if (isUndeclaredMessageType)
        {
            if (shouldFailOnUndeclaredMessageType)
            {
                exception.Should().BeAssignableTo<MessageBusException>();
            }
            else
            {
                exception.Should().BeNull();
            }
        }
        else
        {
            exception.Should().BeNull();
        }

        if (message is SomeMessage someMessage)
        {
            someMessageConsumerMock.Verify(x => x.OnHandle(someMessage), Times.Once);
        }
        someMessageConsumerMock.VerifyNoOtherCalls();

        if (message is ISomeMessageMarkerInterface someMessageInterface)
        {
            someMessageInterfaceConsumerMock.Verify(x => x.OnHandle(someMessageInterface), Times.Once);
        }
        someMessageInterfaceConsumerMock.VerifyNoOtherCalls();

        if (message is SomeDerivedMessage someDerivedMessage)
        {
            someDerivedMessageConsumerMock.Verify(x => x.OnHandle(someDerivedMessage), Times.Once);
        }
        someDerivedMessageConsumerMock.VerifyNoOtherCalls();

        if (message is SomeRequest someRequest)
        {
            someRequestMessageHandlerMock.Verify(x => x.OnHandle(someRequest), Times.Once);
        }
        someRequestMessageHandlerMock.VerifyNoOtherCalls();
    }
}
