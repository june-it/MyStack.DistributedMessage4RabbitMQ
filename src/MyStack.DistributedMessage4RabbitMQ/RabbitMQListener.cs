﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DistributedMessage4RabbitMQ.Subscriptions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DistributedMessage4RabbitMQ
{
    internal class RabbitMQListener : BackgroundService
    {
        private IModel? _channel;
        private readonly IServiceProvider _serviceProvider;
        private readonly IRabbitMQChannelProvider _rabbitMQProvider;
        private readonly IRoutingKeyResolver _routingKeyResolver;
        private readonly ISubscriptionManager _subscriptionManager;
        private readonly ILogger? _logger;
        public RabbitMQListener(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _rabbitMQProvider = _serviceProvider.GetRequiredService<IRabbitMQChannelProvider>();
            _routingKeyResolver = _serviceProvider.GetRequiredService<IRoutingKeyResolver>();
            _subscriptionManager = _serviceProvider.GetRequiredService<ISubscriptionManager>();
            Options = _serviceProvider.GetRequiredService<IOptions<RabbitMQOptions>>().Value;
            var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
            _logger = loggerFactory?.CreateLogger(GetType().Name);

        }
        public RabbitMQOptions Options { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var allSubscriptions = _subscriptionManager.GetAllSubscriptions();
            if (allSubscriptions == null)
                return;

            _channel = _rabbitMQProvider.CreateModel();
            // 定义交换机
            _channel.ExchangeDeclare(Options.ExchangeOptions.Name, Options.ExchangeOptions.ExchangeType, Options.ExchangeOptions.Durable, Options.ExchangeOptions.AutoDelete, Options.ExchangeOptions.Arguments);
            // 定义队列
            _channel.QueueDeclare(Options.QueueOptions.Name, Options.QueueOptions.Durable, Options.QueueOptions.Exclusive, Options.QueueOptions.AutoDelete, Options.QueueOptions.Arguments);
            // 绑定订阅路由键
            foreach (var subscription in allSubscriptions)
            {
                _channel.QueueBind(Options.QueueOptions.Name, Options.ExchangeOptions.Name, subscription.Key);
                _logger?.LogInformation($"绑定路由键`{subscription.Key}`到队列`{Options.QueueOptions.Name}` ");
            }


            EventingBasicConsumer consumer = new EventingBasicConsumer(_channel);
            _channel.BasicConsume(queue: Options.QueueOptions.Name, autoAck: false, consumer);
            consumer.Received += async (_, e) =>
            {
                var receivedMessage = Encoding.UTF8.GetString(e.Body.Span);
                _logger?.LogInformation($"收到消息: {receivedMessage}。");
                if (!allSubscriptions.TryGetValue(e.RoutingKey, out var subscriptions))
                    return;
                if (subscriptions != null)
                {
                    foreach (var subscription in subscriptions)
                    {
                        if (string.IsNullOrEmpty(receivedMessage))
                            continue;
                        object? eventData;
                        if (subscription.MessageType != null)
                            eventData = JsonConvert.DeserializeObject(receivedMessage, subscription.MessageType);
                        else
                            eventData = JsonConvert.DeserializeObject(receivedMessage);
                        if (eventData == null)
                            continue;

                        if (subscription.ResponseType == null)
                        {
                            await DistributedEventHandleAsync(_channel, e, subscription, eventData, cancellationToken);
                        }
                        else
                        {
                            await RpcMessageHandleAsync(_channel, e, subscription, eventData, cancellationToken);
                        }
                    }
                }
            };
            await Task.CompletedTask;
        }

        private async Task RpcMessageHandleAsync(IModel channel, BasicDeliverEventArgs e, SubscriptionInfo subscription, object eventData, CancellationToken cancellationToken)
        {
            string replyMessage = "";
            try
            {
                var eventHandlerType = subscription.InterfaceHandlerType.MakeGenericType(subscription.MessageType, subscription.ResponseType);
                var eventHandler = _serviceProvider.GetRequiredService(eventHandlerType);
                var replyMessageObj = await ((dynamic)eventHandler).HandleAsync((dynamic)eventData, cancellationToken);
                replyMessage = JsonConvert.SerializeObject(replyMessageObj);
            }
            finally
            {
                var properties = e.BasicProperties;
                var replyProperties = channel.CreateBasicProperties();
                replyProperties.CorrelationId = properties.CorrelationId;
                _logger?.LogInformation($"回复消息: {replyMessage}。");
                var replyBytes = Encoding.UTF8.GetBytes(replyMessage);
                channel.BasicPublish(exchange: Options.ExchangeOptions.Name, routingKey: properties.ReplyTo, mandatory: false, basicProperties: replyProperties, body: replyBytes);
                channel.BasicAck(e.DeliveryTag, false);
            }
        }

        private async Task DistributedEventHandleAsync(IModel channel, BasicDeliverEventArgs e, SubscriptionInfo subscription, object eventData, CancellationToken cancellationToken)
        {
            if (typeof(IDistributedEvent).IsAssignableFrom(subscription.MessageType))
            {
                var eventHandlerType = subscription.InterfaceHandlerType.MakeGenericType(subscription.MessageType);
                var eventHandler = _serviceProvider.GetRequiredService(eventHandlerType);
                await ((dynamic)eventHandler).HandleAsync((dynamic)eventData, cancellationToken);
            }
            else if (subscription.MessageType != null)
            {
                var eventWrapperType = typeof(DistributedEventWrapper<>).MakeGenericType(subscription.MessageType);
                var eventHandlerType = subscription.InterfaceHandlerType.MakeGenericType(eventWrapperType);
                var eventHandler = _serviceProvider.GetRequiredService(eventHandlerType);
                var eventWrapper = Activator.CreateInstance(eventWrapperType, eventData);
                await ((dynamic)eventHandler).HandleAsync((dynamic)eventWrapper, cancellationToken);
            }
            else
            {
                var eventHandlerType = subscription.InterfaceHandlerType;
                var eventHandler = _serviceProvider.GetRequiredService(eventHandlerType);
                await ((dynamic)eventHandler).HandleAsync((dynamic)eventData, cancellationToken);
            }
            channel.BasicAck(e.DeliveryTag, false);
        }
    }
}
