﻿namespace SlimMessageBus.Host.Collections;

public class RuntimeTypeCache : IRuntimeTypeCache
{
    private readonly SafeDictionaryWrapper<(Type From, Type To), bool> _isAssignable;

    public GenericInterfaceTypeCache ProducerInterceptorType { get; }
    public GenericInterfaceTypeCache PublishInterceptorType { get; }
    public GenericInterfaceTypeCache2 SendInterceptorType { get; }

    public GenericInterfaceTypeCache ConsumerInterceptorType { get; }
    public GenericInterfaceTypeCache2 HandlerInterceptorType { get; }


    public RuntimeTypeCache()
    {
        _isAssignable = new(x => x.To.IsAssignableFrom(x.From));

        ProducerInterceptorType = new GenericInterfaceTypeCache(typeof(IProducerInterceptor<>), nameof(IProducerInterceptor<object>.OnHandle));
        PublishInterceptorType = new GenericInterfaceTypeCache(typeof(IPublishInterceptor<>), nameof(IPublishInterceptor<object>.OnHandle));
        SendInterceptorType = new GenericInterfaceTypeCache2(typeof(ISendInterceptor<,>), nameof(ISendInterceptor<object, object>.OnHandle));

        ConsumerInterceptorType = new GenericInterfaceTypeCache(typeof(IConsumerInterceptor<>), nameof(IConsumerInterceptor<object>.OnHandle));
        HandlerInterceptorType = new GenericInterfaceTypeCache2(typeof(IRequestHandlerInterceptor<,>), nameof(IRequestHandlerInterceptor<object, object>.OnHandle));
    }

    public bool IsAssignableFrom(Type from, Type to)
        => _isAssignable.GetOrAdd((from, to));
}
