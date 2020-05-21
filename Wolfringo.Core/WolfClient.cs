﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TehGM.Wolfringo.Messages;
using TehGM.Wolfringo.Messages.Serialization;
using TehGM.Wolfringo.Messages.Serialization.Internal;
using TehGM.Wolfringo.Socket;
using TehGM.Wolfringo.Utilities;

namespace TehGM.Wolfringo
{
    public class WolfClient : IWolfClient, IDisposable
    {
        public const string DefaultUrl = "wss://v3-rc.palringo.com:3051";
        public const string DefaultDevice = "bot";

        public string Url { get; }
        public string Token { get; }
        public string Device { get; }
        public bool ThrowMissingSerializer { get; set; } = true;

        public event Action<IWolfMessage> MessageReceived;

        private readonly ISocketClient _client;
        private readonly IDictionary<string, IMessageSerializer> _serializers;
        private readonly IMessageSerializer _fallbackSerializer;

        public WolfClient(string url, string token, string device = DefaultDevice)
        {
            this.Url = url;
            this.Token = token;
            this.Device = device;

            this._serializers = GetDefaultMessageSerializers();
            this._fallbackSerializer = new JsonMessageSerializer<IWolfMessage>();
            this._client = new SocketClient();
            this._client.MessageReceived += OnClientMessageReceived;
            this._client.MessageSent += OnClientMessageSent;
            this._client.Disconnected += _client_Disconnected;
            this._client.ErrorRaised += _client_ErrorRaised;
        }

        private void _client_ErrorRaised(object sender, UnhandledExceptionEventArgs e)
        {
            throw new NotImplementedException();
        }

        private void _client_Disconnected(object sender, SocketClosedEventArgs e)
        {
            throw new NotImplementedException();
        }

        public WolfClient(string token, string device = DefaultDevice)
            : this(DefaultUrl, token, device) { }

        public WolfClient(string device = DefaultDevice)
            : this(DefaultUrl, new DefaultWolfTokenProvider().GenerateToken(18), device) { }

        public Task ConnectAsync()
            => _client.ConnectAsync(new Uri(new Uri(this.Url), $"/socket.io/?token={this.Token}&device={this.Device}&EIO=3&transport=websocket"));

        public Task DisconnectAsync()
            => _client.DisconnectAsync();

        public async Task<TResponse> SendAsync<TResponse>(IWolfMessage message) where TResponse : class
        {
            SerializedMessageData data;
            if (_serializers.TryGetValue(message.Command, out IMessageSerializer serializer))
                data = serializer.Serialize(message);
            else if (!ThrowMissingSerializer)
                throw new KeyNotFoundException($"Serializer for command {message.Command} not found");
            else
                // try fallback simple serialization
                data = _fallbackSerializer.Serialize(message);

            uint msgId = await _client.SendAsync(message.Command, data.Payload, data.BinaryMessages).ConfigureAwait(false);
            return await AwaitResponseAsync<TResponse>(msgId).ConfigureAwait(false);
        }

        private Task<TResponse> AwaitResponseAsync<TResponse>(uint messageId) where TResponse : class
        {
            TaskCompletionSource<TResponse> tcs = new TaskCompletionSource<TResponse>();
            EventHandler<SocketMessageEventArgs> callback = null;
            callback = (sender, e) =>
            {
                try
                {
                    // only accept response with corresponding message ID
                    if (e.Message.ID == null)
                        return;
                    if (e.Message.ID.Value != messageId)
                        return;

                    // parse response
                    JToken responseObject = (e.Message.Payload is JArray) ? e.Message.Payload.First : e.Message.Payload;
                    TResponse response = responseObject.ToObject<TResponse>();

                    // if response has body or headers, further use it to populate the response entity
                    responseObject.PopulateObject(ref response, "headers");
                    responseObject.PopulateObject(ref response, "body");

                    // set task result to finish it, and unhook the event to prevent memory leaks
                    tcs.TrySetResult(response);
                    if (_client != null)
                        _client.MessageReceived -= callback;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    throw;
                }
            };
            _client.MessageReceived += callback;
            return tcs.Task;
        }

        public void Dispose()
            => (_client as IDisposable)?.Dispose();

        private void OnClientMessageReceived(object sender, SocketMessageEventArgs e)
        {
            try
            {
                Console.WriteLine($"< {e.Message}");

                if (e.Message.Type == SocketMessageType.BinaryEvent || e.Message.Type == SocketMessageType.Event)
                {
                    if (e.Message.Payload is JArray array)
                    {
                        string command = array.First.ToObject<string>();
                        if (!_serializers.TryGetValue(command, out IMessageSerializer serializer))
                        {
                            if (ThrowMissingSerializer)
                                throw new KeyNotFoundException($"Serializer for command {command} not found");
                            return;
                        }
                        IWolfMessage msg = serializer.Deserialize(command, new SerializedMessageData(array.First.Next, e.BinaryMessages));
                        if (msg == null)
                            return;
                        this.MessageReceived?.Invoke(msg);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private void OnClientMessageSent(object sender, SocketMessageEventArgs e)
        {
            Console.WriteLine($"> {e.Message}");
        }

        protected virtual IDictionary<string, IMessageSerializer> GetDefaultMessageSerializers()
        {
            return new Dictionary<string, IMessageSerializer>(StringComparer.OrdinalIgnoreCase)
            {
                { MessageCommands.Welcome, new JsonMessageSerializer<WelcomeMessage>() },
                { MessageCommands.Login, new JsonMessageSerializer<LoginMessage>() },
                { MessageCommands.SubscribeToPm, new JsonMessageSerializer<SubscribeToPmMessage>() },
                { MessageCommands.Chat, new ChatMessageSerializer() }
            };
        }

        public void SetSerializer(string command, IMessageSerializer serializer)
            => this._serializers[command] = serializer;
    }
}
