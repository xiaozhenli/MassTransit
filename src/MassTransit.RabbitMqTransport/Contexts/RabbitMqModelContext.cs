﻿// Copyright 2007-2018 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace MassTransit.RabbitMqTransport.Contexts
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using GreenPipes;
    using GreenPipes.Payloads;
    using Integration;
    using Logging;
    using RabbitMQ.Client;
    using RabbitMQ.Client.Events;
    using Topology;
    using Util;


    public class RabbitMqModelContext :
        BasePipeContext,
        ModelContext,
        IAsyncDisposable
    {
        static readonly ILog _log = Logger.Get<RabbitMqModelContext>();

        readonly ConnectionContext _connectionContext;
        readonly IModel _model;
        readonly ConcurrentDictionary<ulong, PendingPublish> _published;
        readonly LimitedConcurrencyLevelTaskScheduler _taskScheduler;
        ulong _publishTagMax;

        public RabbitMqModelContext(ConnectionContext connectionContext, IModel model, CancellationToken cancellationToken)
            : base(new PayloadCacheScope(connectionContext), cancellationToken)
        {
            _connectionContext = connectionContext;
            _model = model;

            _published = new ConcurrentDictionary<ulong, PendingPublish>();
            _taskScheduler = new LimitedConcurrencyLevelTaskScheduler(1);

            _model.ModelShutdown += OnModelShutdown;
            _model.BasicAcks += OnBasicAcks;
            _model.BasicNacks += OnBasicNacks;
            _model.BasicReturn += OnBasicReturn;

            if (_connectionContext.PublisherConfirmation)
                _model.ConfirmSelect();
        }

        public Task DisposeAsync(CancellationToken cancellationToken)
        {
            if (_log.IsDebugEnabled)
                _log.DebugFormat("Closing model: {0} / {1}", _model.ChannelNumber, _connectionContext.Description);

            try
            {
                if (_connectionContext.PublisherConfirmation && _model.IsOpen && _published.Count > 0)
                {
                    bool timedOut;
                    do
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        _model.WaitForConfirms(_connectionContext.StopTimeout, out timedOut);
                        if (timedOut)
                            _log.WarnFormat("Timeout waiting for pending confirms: {0}", _connectionContext.Description);
                        else
                            _log.DebugFormat("Pending confirms complete: {0}", _connectionContext.Description);
                    }
                    while (timedOut);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Fault waiting for confirms", ex);
            }

            _model.Cleanup(200, "ModelContext Disposed");

            return TaskUtil.Completed;
        }

        IModel ModelContext.Model => _model;

        ConnectionContext ModelContext.ConnectionContext => _connectionContext;

        async Task ModelContext.BasicPublishAsync(string exchange, string routingKey, bool mandatory, IBasicProperties basicProperties, byte[] body,
            bool awaitAck)
        {
            if (_connectionContext.PublisherConfirmation)
            {
                var pendingPublish = await Task.Factory.StartNew(() => PublishAsync(exchange, routingKey, mandatory, basicProperties, body),
                    CancellationToken, TaskCreationOptions.None, _taskScheduler).ConfigureAwait(false);

                if (awaitAck)
                {
                    await pendingPublish.Task.ConfigureAwait(false);

                    await Task.Yield();
                }
            }
            else
            {
                await Task.Factory.StartNew(() => Publish(exchange, routingKey, mandatory, basicProperties, body),
                    CancellationToken, TaskCreationOptions.None, _taskScheduler).ConfigureAwait(false);
            }
        }

        Task ModelContext.ExchangeBind(string destination, string source, string routingKey, IDictionary<string, object> arguments)
        {
            return Task.Factory.StartNew(() => _model.ExchangeBind(destination, source, routingKey, arguments),
                CancellationToken, TaskCreationOptions.None, _taskScheduler);
        }

        Task ModelContext.ExchangeDeclare(string exchange, string type, bool durable, bool autoDelete, IDictionary<string, object> arguments)
        {
            return Task.Factory.StartNew(() => _model.ExchangeDeclare(exchange, type, durable, autoDelete, arguments),
                CancellationToken, TaskCreationOptions.None, _taskScheduler);
        }

        public Task ExchangeDeclarePassive(string exchange)
        {
            return Task.Factory.StartNew(() => _model.ExchangeDeclarePassive(exchange),
                CancellationToken, TaskCreationOptions.None, _taskScheduler);
        }

        Task ModelContext.QueueBind(string queue, string exchange, string routingKey, IDictionary<string, object> arguments)
        {
            return Task.Factory.StartNew(() => _model.QueueBind(queue, exchange, routingKey, arguments),
                CancellationToken, TaskCreationOptions.None, _taskScheduler);
        }

        Task<QueueDeclareOk> ModelContext.QueueDeclare(string queue, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object> arguments)
        {
            return Task.Factory.StartNew(() => _model.QueueDeclare(queue, durable, exclusive, autoDelete, arguments),
                CancellationToken, TaskCreationOptions.None, _taskScheduler);
        }

        Task<QueueDeclareOk> ModelContext.QueueDeclarePassive(string queue)
        {
            return Task.Factory.StartNew(() => _model.QueueDeclarePassive(queue), CancellationToken, TaskCreationOptions.None, _taskScheduler);
        }

        Task<uint> ModelContext.QueuePurge(string queue)
        {
            return Task.Factory.StartNew(() => _model.QueuePurge(queue),
                CancellationToken, TaskCreationOptions.None, _taskScheduler);
        }

        Task ModelContext.BasicQos(uint prefetchSize, ushort prefetchCount, bool global)
        {
            return Task.Factory.StartNew(() => _model.BasicQos(prefetchSize, prefetchCount, global),
                CancellationToken, TaskCreationOptions.None, _taskScheduler);
        }

        void ModelContext.BasicAck(ulong deliveryTag, bool multiple)
        {
            _model.BasicAck(deliveryTag, multiple);
        }

        void ModelContext.BasicNack(ulong deliveryTag, bool multiple, bool requeue)
        {
            _model.BasicNack(deliveryTag, multiple, requeue);
        }

        Task<string> ModelContext.BasicConsume(string queue, bool noAck, bool exclusive, IDictionary<string, object> arguments, IBasicConsumer consumer)
        {
            return Task.Factory.StartNew(() => _model.BasicConsume(consumer, queue, noAck, "", false, exclusive, arguments),
                CancellationToken, TaskCreationOptions.None, _taskScheduler);
        }

        public Task BasicCancel(string consumerTag)
        {
            return Task.Factory.StartNew(() => _model.BasicCancel(consumerTag),
                CancellationToken, TaskCreationOptions.None, _taskScheduler);
        }

        void OnBasicReturn(object model, BasicReturnEventArgs args)
        {
            if (_log.IsDebugEnabled)
                _log.DebugFormat("BasicReturn: {0}-{1} {2}", args.ReplyCode, args.ReplyText, args.BasicProperties.MessageId);

            if (args.BasicProperties.Headers.TryGetValue("publishId", out var value))
            {
                var bytes = value as byte[];
                if (bytes == null)
                    return;

                if (!ulong.TryParse(Encoding.UTF8.GetString(bytes), out var id))
                    return;

                if (_published.TryRemove(id, out var published))
                    published.PublishReturned(args.ReplyCode, args.ReplyText);
            }
        }

        PendingPublish PublishAsync(string exchange, string routingKey, bool mandatory, IBasicProperties basicProperties, byte[] body)
        {
            var publishTag = _model.NextPublishSeqNo;
            _publishTagMax = Math.Max(_publishTagMax, publishTag);
            var pendingPublish = new PendingPublish(_connectionContext, exchange, publishTag);
            try
            {
                _published.AddOrUpdate(publishTag, key => pendingPublish, (key, existing) =>
                {
                    existing.PublishNotConfirmed($"Duplicate key: {key}");

                    return pendingPublish;
                });

                basicProperties.Headers["publishId"] = publishTag.ToString("F0");

                _model.BasicPublish(exchange, routingKey, mandatory, basicProperties, body);
            }
            catch
            {
                _published.TryRemove(publishTag, out _);

                throw;
            }

            return pendingPublish;
        }

        void Publish(string exchange, string routingKey, bool mandatory, IBasicProperties basicProperties, byte[] body)
        {
            var publishTag = _model.NextPublishSeqNo;
            _publishTagMax = Math.Max(_publishTagMax, publishTag);

            _model.BasicPublish(exchange, routingKey, mandatory, basicProperties, body);
        }

        void OnModelShutdown(object model, ShutdownEventArgs reason)
        {
            _model.ModelShutdown -= OnModelShutdown;
            _model.BasicAcks -= OnBasicAcks;
            _model.BasicNacks -= OnBasicNacks;
            _model.BasicReturn -= OnBasicReturn;

            FaultPendingPublishes(reason.ReplyText);
        }

        void FaultPendingPublishes(string reason)
        {
            try
            {
                foreach (var key in _published.Keys)
                {
                    if (_published.TryRemove(key, out var pending))
                        pending.PublishNotConfirmed(reason);
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        void OnBasicNacks(object model, BasicNackEventArgs args)
        {
            if (args.Multiple)
            {
                ulong[] ids = _published.Keys.Where(x => x <= args.DeliveryTag).ToArray();
                foreach (var id in ids)
                {
                    if (_published.TryRemove(id, out var value))
                        value.Nack();
                }
            }
            else
            {
                if (_published.TryRemove(args.DeliveryTag, out var value))
                    value.Nack();
            }
        }

        void OnBasicAcks(object model, BasicAckEventArgs args)
        {
            if (args.Multiple)
            {
                ulong[] ids = _published.Keys.Where(x => x <= args.DeliveryTag).ToArray();
                foreach (var id in ids)
                {
                    if (_published.TryRemove(id, out var value))
                        value.Ack();
                }
            }
            else
            {
                if (_published.TryRemove(args.DeliveryTag, out var value))
                    value.Ack();
            }
        }
    }
}