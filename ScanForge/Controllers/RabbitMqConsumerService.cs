using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScanForge.Service {
    public class RabbitMqConsumerService : BackgroundService {
        private IConnection _connection;
        private IModel _channel;

        public RabbitMqConsumerService() {
            var factory = new ConnectionFactory() {
                HostName = "rabbitmq", // nome do container no docker-compose
                UserName = "admin",
                Password = "admin",
                Port = 5672
            };

            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            // Cria a fila (idempotente)
            _channel.QueueDeclare(queue: "minha_fila",
                                  durable: false,
                                  exclusive: false,
                                  autoDelete: false,
                                  arguments: null);
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken) {
            var consumer = new EventingBasicConsumer(_channel);

            consumer.Received += (model, ea) => {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                Console.WriteLine($"Mensagem recebida: {message}");
            };

            _channel.BasicConsume(queue: "minha_fila",
                                  autoAck: true,
                                  consumer: consumer);

            return Task.CompletedTask;
        }

        public override void Dispose() {
            _channel?.Close();
            _connection?.Close();
            base.Dispose();
        }
    }
}
