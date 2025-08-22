using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Text;

namespace ScanForge.Service {
    public class Consumer {
        public static void StartConsuming() {
            // Configurações do RabbitMQ
            var factory = new ConnectionFactory() {
                HostName = "rabbitmq", // nome do container definido no docker-compose
                UserName = "admin",
                Password = "admin",
                Port = 5672
            };

            using var connection = factory.CreateConnection();
            using var channel = connection.CreateModel();

            // Declara a fila (idempotente, só cria se não existir)
            channel.QueueDeclare(queue: "minha_fila",
                                 durable: true,
                                 exclusive: false,
                                 autoDelete: false,
                                 arguments: null);

            var consumer = new EventingBasicConsumer(channel);

            consumer.Received += (model, ea) => {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                Console.WriteLine($"Mensagem recebida: {message}");
            };

            // Começa a consumir
            channel.BasicConsume(queue: "minha_fila",
                                 autoAck: true,
                                 consumer: consumer);

            Console.WriteLine("Consumidor iniciado. Pressione [enter] para sair.");
            Console.ReadLine(); // mantém o app rodando
        }
    }
}
