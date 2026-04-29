using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
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

        public JsonSerializerOptions SerializeOptions { get; }

        // Events
        public event EventHandler<HandshakeReceivedEventArgs> HandshakeReceived;
        public event EventHandler<CloseApplicationEventArgs> CloseApplication;
        public event EventHandler<FormSubmittedEventArgs<TQuestionnaireResponse, TOperationOutcome>> FormSubmitted;

        private readonly ConcurrentDictionary<string, Func<SmartMessageResponse, Task>> _responseListeners
            = new ConcurrentDictionary<string, Func<SmartMessageResponse, Task>>();

        /// <summary>
        /// Transport delegate called for every outbound envelope. Fire-and-forget — the
        /// returned <see cref="Task"/> represents the local enqueue / post operation only,
        /// not the protocol response. Responses arrive asynchronously via inbound messages
        /// and are dispatched through the per-request listener registered with
        /// <see cref="SendRequestAsync(SmartMessageRequest, Func{SmartMessageResponse, Task}, CancellationToken)"/>.
        /// </summary>
        public delegate Task MessageSender(string jsonMessage);
        public MessageSender SendMessage { get; set; }

        /// <summary>
        /// Optional hook called immediately before any outbound <see cref="SmartMessageBase"/>
        /// is serialized. Used to attach <c>_meta</c> (e.g. Sentry trace propagation).
        /// Exceptions from the provider are swallowed — telemetry must never break a send.
        /// </summary>
        public Func<SmartMessageBase, MessageMeta> MetaProvider { get; set; }

        private void ApplyMeta(SmartMessageBase message)
        {
            if (message == null || MetaProvider == null) return;
            try { message.Meta = MetaProvider(message); }
            catch { /* never let enrichment break a send */ }
        }

        // ---------------------------------------------------------------------------
        // Constructor — concrete subclasses pass their FHIR-version-specific options
        // ---------------------------------------------------------------------------
        protected SmartMessageHandlerBase(ILogger logger, JsonSerializerOptions serializeOptions)
        {
            _logger = logger ?? NullLogger.Instance;
            SerializeOptions = serializeOptions ?? throw new ArgumentNullException(nameof(serializeOptions));
            EnsurePayloadResolver(SerializeOptions);
        }

        private static void EnsurePayloadResolver(JsonSerializerOptions options)
        {
            if (options.TypeInfoResolver is PayloadTypeInfoResolver<TResource, TQuestionnaireResponse>)
                return;
            var inner = options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
            options.TypeInfoResolver = new PayloadTypeInfoResolver<TResource, TQuestionnaireResponse>(inner);
        }

        protected virtual void OnFormSubmitted(TQuestionnaireResponse response, TOperationOutcome outcome)
        {
            FormSubmitted?.Invoke(this, new FormSubmittedEventArgs<TQuestionnaireResponse, TOperationOutcome>(response, outcome));
        }

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
                        _ = HandleResponseMessageAsync(response);
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
                ApplyMeta(response);
                return JsonSerializer.Serialize(response, SerializeOptions);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unhandled exception during message handling.");
                try
                {
                    string messageId = GetMessageIdFromJson(jsonMessage);
                    var response = SmartMessageResponse.CreateErrorResponse(messageId, new ErrorResponse(e));
                    ApplyMeta(response);
                    return JsonSerializer.Serialize(response, SerializeOptions);
                }
                catch
                {
                    var response = SmartMessageResponse.CreateErrorResponse(null, new ErrorResponse(e));
                    ApplyMeta(response);
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
                    case "status.handshake":
                        _logger.LogDebug("Handling status.handshake.");
                        response = HandleHandshake(message, message.Payload);
                        break;

                    case "form.submitted":
                        _logger.LogDebug("Handling form.submitted.");
                        var formPayload = JsonSerializer.Deserialize<FormSubmit<TQuestionnaireResponse, TOperationOutcome>>(payloadJson, SerializeOptions);
                        if (formPayload == null)
                            throw new ValidationException("form.submitted payload was null.");
                        // [Required] attributes aren't enforced by System.Text.Json — validate
                        // explicitly before raising FormSubmitted so subscribers never see null
                        // Response or Outcome. Outer catch turns ValidationException into an
                        // error response.
                        Validator.ValidateObject(formPayload, new ValidationContext(formPayload), validateAllProperties: true);
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

            ApplyMeta(response);
            var responseMessage = JsonSerializer.Serialize(response, SerializeOptions);
            _logger.LogInformation("Created response: {responseMessage}", responseMessage);
            return responseMessage;
        }

        // ---------------------------------------------------------------------------
        // Individual request handlers
        // ---------------------------------------------------------------------------
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
        // Fire-and-forget from the sync HandleMessage caller (discard return via `_ =`).
        // Outer try/catch is the backstop: this runs with no awaiter to observe faults,
        // so anything that escapes would hit TaskScheduler.UnobservedTaskException / the
        // UI thread's ThreadException handler and can crash the app.
        private async Task HandleResponseMessageAsync(SmartMessageResponse responseMessage)
        {
            try
            {
                if (responseMessage == null) return;
                if (string.IsNullOrEmpty(responseMessage.ResponseToMessageId))
                {
                    // Malformed inbound (the routing scan saw a top-level
                    // "responseToMessageId" key with null/empty value). ConcurrentDictionary
                    // would throw ArgumentNullException on the lookup; bail cleanly instead.
                    _logger.LogWarning("Inbound response missing ResponseToMessageId; dropping.");
                    return;
                }
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
            catch (Exception e)
            {
                try { _logger.LogError(e, "Unhandled exception in response handler"); } catch { /* never rethrow from a fire-and-forget trap */ }
            }
        }

        // ---------------------------------------------------------------------------
        // Send helpers
        // ---------------------------------------------------------------------------
        public Task SendRequestAsync(
            SmartMessageRequest request,
            Func<SmartMessageResponse, Task> responseHandler = null,
            CancellationToken cancellationToken = default)
        {
            if (SendMessage == null)
                throw new InvalidOperationException("SendMessage delegate must be set before sending requests");

            cancellationToken.ThrowIfCancellationRequested();

            if (responseHandler != null)
            {
                _responseListeners[request.MessageId] = responseHandler;
                // If the caller later cancels, drop the listener so the response (if it ever arrives)
                // is ignored. Cleanup on successful response still happens in HandleResponseMessage;
                // double-remove is harmless.
                if (cancellationToken.CanBeCanceled)
                    cancellationToken.Register(
                        state => _responseListeners.TryRemove((string)state, out _),
                        request.MessageId);
            }

            ApplyMeta(request);
            string requestJson = JsonSerializer.Serialize(request, SerializeOptions);
            return SendMessage(requestJson);
        }

        public Task SendRequestAsync(
            string messageType,
            RequestPayload payload,
            Func<SmartMessageResponse, Task> responseHandler = null,
            CancellationToken cancellationToken = default)
        {
            var message = new SmartMessageRequest(Guid.NewGuid().ToString(), "smart-web-messaging", messageType, payload);
            return SendRequestAsync(message, responseHandler, cancellationToken);
        }

        public Task SendMessageAsync(
            string messageType,
            RequestPayload payload = null,
            Func<SmartMessageResponse, Task> responseHandler = null,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Sending {MessageType}", messageType);
            var message = new SmartMessageRequest(Guid.NewGuid().ToString(), "smart-web-messaging", messageType, payload ?? new RequestPayload());
            return SendRequestAsync(message, responseHandler, cancellationToken);
        }

        public Task SendFormRequestSubmitAsync(
            Func<SmartMessageResponse, Task> responseHandler = null,
            CancellationToken cancellationToken = default)
        {
            return SendMessageAsync("ui.form.requestSubmit", new RequestPayload(), responseHandler, cancellationToken);
        }

        public Task SendFormPersistAsync(
            Func<SmartMessageResponse, Task> responseHandler = null,
            CancellationToken cancellationToken = default)
        {
            return SendMessageAsync("ui.form.persist", new RequestPayload(), responseHandler, cancellationToken);
        }

        public Task SendSdcConfigureAsync(
            string terminologyServer = null,
            string dataServer = null,
            object configuration = null,
            Func<SmartMessageResponse, Task> responseHandler = null,
            CancellationToken cancellationToken = default)
        {
            var payload = new SdcConfigure(terminologyServer, dataServer, configuration);
            return SendMessageAsync("sdc.configure", payload, responseHandler, cancellationToken);
        }

        public Task SendSdcConfigureContextAsync(
            ResourceReference subject = null,
            ResourceReference author = null,
            ResourceReference encounter = null,
            List<LaunchContext<TResource>> launchContext = null,
            Func<SmartMessageResponse, Task> responseHandler = null,
            CancellationToken cancellationToken = default)
        {
            var payload = new SdcConfigureContext<TResource>(subject, author, encounter, launchContext);
            return SendMessageAsync("sdc.configureContext", payload, responseHandler, cancellationToken);
        }

        /// <summary>
        /// Sends <c>sdc.configureContext</c>, wrapping the supplied FHIR resources in a launch context
        /// (names: "patient", "encounter", "user"). Each resource parameter is optional.
        /// </summary>
        public Task SendSdcConfigureContextAsync(
            TResource patient = default,
            TResource encounter = default,
            TResource author = default,
            Func<SmartMessageResponse, Task> responseHandler = null,
            CancellationToken cancellationToken = default)
        {
            return SendSdcConfigureContextAsync(
                launchContext: BuildLaunchContext(patient, encounter, author),
                responseHandler: responseHandler,
                cancellationToken: cancellationToken);
        }

        public Task SendSdcDisplayQuestionnaireAsync(
            object questionnaire,
            TQuestionnaireResponse questionnaireResponse = default,
            ResourceReference subject = null,
            ResourceReference author = null,
            ResourceReference encounter = null,
            List<LaunchContext<TResource>> launchContext = null,
            Func<SmartMessageResponse, Task> responseHandler = null,
            CancellationToken cancellationToken = default)
        {
            var context = new SdcDisplayQuestionnaireContext<TResource>(subject, author, encounter, launchContext);
            var payload = new SdcDisplayQuestionnaire<TResource, TQuestionnaireResponse>(questionnaire, questionnaireResponse, context);
            return SendMessageAsync("sdc.displayQuestionnaire", payload, responseHandler, cancellationToken);
        }

        /// <summary>
        /// Sends <c>sdc.displayQuestionnaire</c> with a <typeparamref name="TResource"/> questionnaire
        /// and FHIR-resource launch context (patient/encounter/user).
        /// </summary>
        public Task SendSdcDisplayQuestionnaireAsync(
            TResource questionnaire,
            TQuestionnaireResponse questionnaireResponse = default,
            TResource patient = default,
            TResource encounter = default,
            TResource author = default,
            Func<SmartMessageResponse, Task> responseHandler = null,
            CancellationToken cancellationToken = default)
        {
            return SendSdcDisplayQuestionnaireAsync(
                questionnaire: (object)questionnaire,
                questionnaireResponse: questionnaireResponse,
                launchContext: BuildLaunchContext(patient, encounter, author),
                responseHandler: responseHandler,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Sends <c>sdc.displayQuestionnaire</c> with a canonical URL reference to the questionnaire
        /// and FHIR-resource launch context (patient/encounter/user).
        /// </summary>
        public Task SendSdcDisplayQuestionnaireAsync(
            string questionnaireCanonicalUrl,
            TQuestionnaireResponse questionnaireResponse = default,
            TResource patient = default,
            TResource encounter = default,
            TResource author = default,
            Func<SmartMessageResponse, Task> responseHandler = null,
            CancellationToken cancellationToken = default)
        {
            return SendSdcDisplayQuestionnaireAsync(
                questionnaire: (object)questionnaireCanonicalUrl,
                questionnaireResponse: questionnaireResponse,
                launchContext: BuildLaunchContext(patient, encounter, author),
                responseHandler: responseHandler,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Builds a launch-context list from optional patient/encounter/user resources.
        /// Returns <c>null</c> if none are supplied, so the emitted payload omits the list entirely.
        /// </summary>
        protected static List<LaunchContext<TResource>> BuildLaunchContext(
            TResource patient, TResource encounter, TResource author)
        {
            var ctx = new List<LaunchContext<TResource>>();
            if (patient != null) ctx.Add(new LaunchContext<TResource>("patient", contentResource: patient));
            if (encounter != null) ctx.Add(new LaunchContext<TResource>("encounter", contentResource: encounter));
            if (author != null) ctx.Add(new LaunchContext<TResource>("user", contentResource: author));
            return ctx.Count > 0 ? ctx : null;
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
        public string GetMessageIdFromJson(string json) => JsonProbe.ExtractStringField(json, "messageId");
    }
}
