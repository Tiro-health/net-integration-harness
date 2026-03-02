using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tiro.Health.SmartWebMessaging.Events;
using Tiro.Health.SmartWebMessaging.Message;
using Tiro.Health.SmartWebMessaging.Message.Payload;

namespace Tiro.Health.SmartWebMessaging
{
    /// <summary>
    /// Abstract base class implementing all SMART Web Messaging routing and correlation logic.
    /// Bind TResource, TQuestionnaireResponse, and TOperationOutcome to concrete FHIR types
    /// in a derived class (e.g., Tiro.Health.SmartWebMessaging.Fhir.R5.SmartMessageHandler).
    /// </summary>
    public abstract class SmartMessageHandlerBase<TResource, TQuestionnaireResponse, TOperationOutcome>
        where TResource : Resource
    {
        private readonly ILogger _logger;
        private readonly Scratchpad<TResource> _scratchpad = new Scratchpad<TResource>();

        public JsonSerializerOptions SerializeOptions { get; }

        // FHIR-free events declared on the base class
        public event EventHandler<HandshakeReceivedEventArgs> HandshakeReceived;
        public event EventHandler<CloseApplicationEventArgs> CloseApplication;

        private readonly ConcurrentDictionary<string, Func<SmartMessageResponse, Task>> _responseListeners
            = new ConcurrentDictionary<string, Func<SmartMessageResponse, Task>>();

        public delegate Task<string> MessageSender(string jsonMessage);
        public MessageSender SendMessage { get; set; }

        // ---------------------------------------------------------------------------
        // Constructor — concrete subclasses pass their FHIR-version-specific options
        // ---------------------------------------------------------------------------
        protected SmartMessageHandlerBase(ILogger logger, JsonSerializerOptions serializeOptions)
        {
            _logger = logger ?? NullLogger.Instance;
            SerializeOptions = serializeOptions ?? throw new ArgumentNullException(nameof(serializeOptions));
        }

        // ---------------------------------------------------------------------------
        // Template methods — override in concrete class to raise typed events
        // ---------------------------------------------------------------------------
        protected virtual void OnFormSubmitted(TQuestionnaireResponse response, TOperationOutcome outcome) { }
        protected virtual void OnResourceChanged(TResource resource) { }

        // ---------------------------------------------------------------------------
        // Public message entry point
        // ---------------------------------------------------------------------------
        public string HandleMessage(string jsonMessage)
        {
            _logger.LogDebug("Received message: {jsonMessage}", jsonMessage);
            try
            {
                SmartMessageBase message;
                if (jsonMessage.Contains("\"responseToMessageId\""))
                {
                    _logger.LogDebug("Message identified as SmartMessageResponse.");
                    message = JsonSerializer.Deserialize<SmartMessageResponse>(jsonMessage, SerializeOptions);
                }
                else
                {
                    _logger.LogDebug("Message identified as SmartMessageRequest.");
                    message = JsonSerializer.Deserialize<SmartMessageRequest>(jsonMessage, SerializeOptions);
                }

                _logger.LogInformation("Handling message of type: {MessageType}", message?.GetType().Name);

                switch (message)
                {
                    case SmartMessageRequest request:
                        return HandleRequestMessage(request);

                    case SmartMessageResponse response:
                        HandleResponseMessage(response);
                        return null;

                    default:
                        throw new InvalidOperationException($"Unknown message type: {message?.GetType().Name}");
                }
            }
            catch (JsonException e)
            {
                _logger.LogError(e, "Failed to deserialize message. JSON: {jsonMessage}", jsonMessage);
                string messageId = GetMessageIdFromJson(jsonMessage);
                var response = SmartMessageResponse.CreateErrorResponse(messageId, new ErrorResponse(e));
                return JsonSerializer.Serialize(response, SerializeOptions);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unhandled exception during message handling.");
                try
                {
                    string messageId = GetMessageIdFromJson(jsonMessage);
                    var response = SmartMessageResponse.CreateErrorResponse(messageId, new ErrorResponse(e));
                    return JsonSerializer.Serialize(response, SerializeOptions);
                }
                catch
                {
                    var response = SmartMessageResponse.CreateErrorResponse(null, new ErrorResponse(e));
                    return JsonSerializer.Serialize(response, SerializeOptions);
                }
            }
        }

        // ---------------------------------------------------------------------------
        // Request routing
        // ---------------------------------------------------------------------------
        private string HandleRequestMessage(SmartMessageRequest message)
        {
            SmartMessageResponse response = null;
            try
            {
                Validator.ValidateObject(message, new ValidationContext(message), true);
                string payloadJson = JsonSerializer.Serialize(message.Payload, SerializeOptions);

                switch (message.MessageType)
                {
                    case "scratchpad.create":
                        _logger.LogDebug("Handling scratchpad.create.");
                        var createPayload = JsonSerializer.Deserialize<ScratchpadCreate<TResource>>(payloadJson, SerializeOptions);
                        response = HandleScratchpadCreate(message, createPayload);
                        break;

                    case "scratchpad.update":
                        _logger.LogDebug("Handling scratchpad.update.");
                        var updatePayload = JsonSerializer.Deserialize<ScratchpadUpdate<TResource>>(payloadJson, SerializeOptions);
                        response = HandleScratchpadUpdate(message, updatePayload);
                        break;

                    case "scratchpad.delete":
                        _logger.LogDebug("Handling scratchpad.delete.");
                        var deletePayload = JsonSerializer.Deserialize<ScratchpadDelete>(payloadJson, SerializeOptions);
                        response = HandleScratchpadDelete(message, deletePayload);
                        break;

                    case "scratchpad.read":
                        _logger.LogDebug("Handling scratchpad.read.");
                        var readPayload = JsonSerializer.Deserialize<ScratchpadRead>(payloadJson, SerializeOptions);
                        response = HandleScratchpadRead(message, readPayload);
                        break;

                    case "status.handshake":
                        _logger.LogDebug("Handling status.handshake.");
                        response = HandleHandshake(message, message.Payload);
                        break;

                    case "form.submitted":
                        _logger.LogDebug("Handling form.submitted.");
                        var formPayload = JsonSerializer.Deserialize<FormSubmit<TQuestionnaireResponse, TOperationOutcome>>(payloadJson, SerializeOptions);
                        response = HandleFormSubmit(message, formPayload);
                        break;

                    case "ui.done":
                        _logger.LogDebug("Handling ui.done.");
                        response = HandleUiDone(message);
                        break;

                    default:
                        response = SmartMessageResponse.CreateErrorResponse(
                            message.MessageId,
                            new ErrorResponse($"Unknown messageType: {message.MessageType}", "UnknownMessageTypeException"));
                        break;
                }
            }
            catch (ValidationException e)
            {
                _logger.LogError(e, "Validation error for MessageId: {MessageId}", message.MessageId);
                response = SmartMessageResponse.CreateErrorResponse(message.MessageId, new ErrorResponse(e));
            }
            catch (DeserializationFailedException e)
            {
                _logger.LogError(e, "Deserialization error for MessageId: {MessageId}", message.MessageId);
                response = SmartMessageResponse.CreateErrorResponse(message.MessageId, new ErrorResponse(e));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unhandled exception for MessageId: {MessageId}", message.MessageId);
                response = SmartMessageResponse.CreateErrorResponse(message.MessageId, new ErrorResponse(e));
            }

            var responseMessage = JsonSerializer.Serialize(response, SerializeOptions);
            _logger.LogInformation("Created response: {responseMessage}", responseMessage);
            return responseMessage;
        }

        // ---------------------------------------------------------------------------
        // Individual request handlers
        // ---------------------------------------------------------------------------
        private SmartMessageResponse HandleScratchpadCreate(SmartMessageRequest message, ScratchpadCreate<TResource> payload)
        {
            string location = _scratchpad.CreateResource(payload.Resource);
            OnResourceChanged(payload.Resource);
            _logger.LogDebug("ResourceChanged raised for MessageId: {MessageId}", message.MessageId);
            return new SmartMessageResponse(Guid.NewGuid().ToString(), message.MessageId, false,
                new ScratchpadCreateResponse<TOperationOutcome>("201 Created", location, default));
        }

        private SmartMessageResponse HandleScratchpadUpdate(SmartMessageRequest message, ScratchpadUpdate<TResource> payload)
        {
            _scratchpad.UpdateResource(payload.Resource);
            OnResourceChanged(payload.Resource);
            _logger.LogDebug("ResourceChanged raised for MessageId: {MessageId}", message.MessageId);
            return new SmartMessageResponse(Guid.NewGuid().ToString(), message.MessageId, false,
                new ScratchpadUpdateResponse<TOperationOutcome>("200 OK", default));
        }

        private SmartMessageResponse HandleScratchpadDelete(SmartMessageRequest message, ScratchpadDelete payload)
        {
            _scratchpad.DeleteResource(payload.Location);
            return new SmartMessageResponse(Guid.NewGuid().ToString(), message.MessageId, false,
                new ScratchpadDeleteResponse<TOperationOutcome>("200 OK", default));
        }

        private SmartMessageResponse HandleScratchpadRead(SmartMessageRequest message, ScratchpadRead payload)
        {
            if (payload.Location == null)
            {
                IEnumerable<TResource> resources = _scratchpad.GetAllResources();
                return new SmartMessageResponse(Guid.NewGuid().ToString(), message.MessageId, false,
                    new ScratchpadReadResponse<TResource, TOperationOutcome>(default, resources, default));
            }
            else
            {
                TResource resource = _scratchpad.GetResource(payload.Location);
                return new SmartMessageResponse(Guid.NewGuid().ToString(), message.MessageId, false,
                    new ScratchpadReadResponse<TResource, TOperationOutcome>(resource, null, default));
            }
        }

        private SmartMessageResponse HandleHandshake(SmartMessageRequest message, RequestPayload payload)
        {
            _logger.LogDebug("Raising HandshakeReceived for MessageId: {MessageId}", message.MessageId);
            HandshakeReceived?.Invoke(this, new HandshakeReceivedEventArgs(message, payload));
            return new SmartMessageResponse(Guid.NewGuid().ToString(), message.MessageId, false, new ResponsePayload());
        }

        private SmartMessageResponse HandleFormSubmit(SmartMessageRequest message, FormSubmit<TQuestionnaireResponse, TOperationOutcome> payload)
        {
            _logger.LogDebug("Raising OnFormSubmitted for MessageId: {MessageId}", message.MessageId);
            OnFormSubmitted(payload.Response, payload.Outcome);
            return new SmartMessageResponse(Guid.NewGuid().ToString(), message.MessageId, false, new ResponsePayload());
        }

        private SmartMessageResponse HandleUiDone(SmartMessageRequest message)
        {
            _logger.LogDebug("Raising CloseApplication for MessageId: {MessageId}", message.MessageId);
            CloseApplication?.Invoke(this, new CloseApplicationEventArgs());
            return new SmartMessageResponse(Guid.NewGuid().ToString(), message.MessageId, false, new ResponsePayload());
        }

        // ---------------------------------------------------------------------------
        // Response handling
        // ---------------------------------------------------------------------------
        private async void HandleResponseMessage(SmartMessageResponse responseMessage)
        {
            _logger.LogInformation("Handling response for ResponseToMessageId: {Id}", responseMessage.ResponseToMessageId);
            if (_responseListeners.TryGetValue(responseMessage.ResponseToMessageId, out var listener))
            {
                try
                {
                    await listener(responseMessage);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Exception in response listener for ResponseToMessageId: {Id}", responseMessage.ResponseToMessageId);
                }
                finally
                {
                    if (!responseMessage.AdditionalResponsesExpected)
                        _responseListeners.TryRemove(responseMessage.ResponseToMessageId, out _);
                }
            }
            else
            {
                _logger.LogWarning("No listener found for ResponseToMessageId: {Id}", responseMessage.ResponseToMessageId);
            }
        }

        // ---------------------------------------------------------------------------
        // Send helpers
        // ---------------------------------------------------------------------------
        public async Task<string> SendRequestAsync(SmartMessageRequest request, Func<SmartMessageResponse, Task> responseHandler = null)
        {
            if (SendMessage == null)
                throw new InvalidOperationException("SendMessage delegate must be set before sending requests");

            if (responseHandler != null)
                _responseListeners[request.MessageId] = responseHandler;

            string requestJson = JsonSerializer.Serialize(request, SerializeOptions);
            return await SendMessage(requestJson);
        }

        public async Task<string> SendRequestAsync(string messageType, RequestPayload payload, Func<SmartMessageResponse, Task> responseHandler = null)
        {
            var message = new SmartMessageRequest(Guid.NewGuid().ToString(), "smart-web-messaging", messageType, payload);
            return await SendRequestAsync(message, responseHandler);
        }

        public async Task<string> SendMessageAsync(string messageType, RequestPayload payload = null, Func<SmartMessageResponse, Task> responseHandler = null)
        {
            _logger.LogInformation("Sending {MessageType}", messageType);
            if (SendMessage == null)
                throw new InvalidOperationException("SendMessage delegate must be set before sending messages");

            var message = new SmartMessageRequest(Guid.NewGuid().ToString(), "smart-web-messaging", messageType, payload ?? new RequestPayload());

            if (responseHandler != null)
                _responseListeners[message.MessageId] = responseHandler;

            string requestJson = JsonSerializer.Serialize(message, SerializeOptions);
            return await SendMessage(requestJson);
        }

        public async Task<string> SendFormRequestSubmitAsync(Func<SmartMessageResponse, Task> responseHandler = null)
        {
            return await SendMessageAsync("ui.form.requestSubmit", new RequestPayload(), responseHandler);
        }

        public async Task<string> SendFormPersistAsync(Func<SmartMessageResponse, Task> responseHandler = null)
        {
            return await SendMessageAsync("ui.form.persist", new RequestPayload(), responseHandler);
        }

        public async Task<string> SendSdcConfigureAsync(
            string terminologyServer = null,
            string dataServer = null,
            object configuration = null,
            Func<SmartMessageResponse, Task> responseHandler = null)
        {
            var payload = new SdcConfigure(terminologyServer, dataServer, configuration);
            return await SendMessageAsync("sdc.configure", payload, responseHandler);
        }

        public async Task<string> SendSdcConfigureContextAsync(
            ResourceReference subject = null,
            ResourceReference author = null,
            ResourceReference encounter = null,
            List<LaunchContext<TResource>> launchContext = null,
            Func<SmartMessageResponse, Task> responseHandler = null)
        {
            var payload = new SdcConfigureContext<TResource>(subject, author, encounter, launchContext);
            return await SendMessageAsync("sdc.configureContext", payload, responseHandler);
        }

        public async Task<string> SendSdcDisplayQuestionnaireAsync(
            object questionnaire,
            TQuestionnaireResponse questionnaireResponse = default,
            ResourceReference subject = null,
            ResourceReference author = null,
            ResourceReference encounter = null,
            List<LaunchContext<TResource>> launchContext = null,
            Func<SmartMessageResponse, Task> responseHandler = null)
        {
            var context = new SdcDisplayQuestionnaireContext<TResource>(subject, author, encounter, launchContext);
            var payload = new SdcDisplayQuestionnaire<TResource, TQuestionnaireResponse>(questionnaire, questionnaireResponse, context);
            return await SendMessageAsync("sdc.displayQuestionnaire", payload, responseHandler);
        }

        // ---------------------------------------------------------------------------
        // Listener management
        // ---------------------------------------------------------------------------
        public void RegisterResponseListener(string messageId, Func<SmartMessageResponse, Task> responseHandler)
        {
            _responseListeners[messageId] = responseHandler;
        }

        public void UnregisterResponseListener(string messageId)
        {
            if (!_responseListeners.TryRemove(messageId, out _))
                _logger.LogWarning("Attempted to unregister non-existent listener for MessageId: {MessageId}", messageId);
        }

        public bool HasPendingResponseListener(string messageId)
        {
            return _responseListeners.ContainsKey(messageId);
        }

        public void ClearAllResponseListeners()
        {
            _responseListeners.Clear();
        }

        // ---------------------------------------------------------------------------
        // Utilities
        // ---------------------------------------------------------------------------
        public string GetMessageIdFromJson(string json)
        {
            var match = Regex.Match(json, "\"messageId\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
