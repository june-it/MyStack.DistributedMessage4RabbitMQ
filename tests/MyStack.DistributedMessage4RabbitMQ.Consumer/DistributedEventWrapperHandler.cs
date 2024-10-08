﻿using Microsoft.Extensions.DistributedMessage4RabbitMQ;
using MyStack.DistributedMessage4RabbitMQ.Shared;

namespace MyStack.DistributedMessage4RabbitMQ.Consumer
{
    public class DistributedEventWrapperHandler : IDistributedEventHandler<DistributedEventWrapper<WrappedData>>
    {
        public async Task HandleAsync(DistributedEventWrapper<WrappedData> eventData, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Console.WriteLine("DistributedEventWrapper");
        }
    }
}
