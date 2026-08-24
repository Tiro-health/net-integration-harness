/*
 * Layer 1 spike: the .NET host's side of SMART Web Messaging, in the page.
 *
 * Installed as window.chrome.webview BEFORE the bridge runs, so the real shipped
 * tiro-swm-bridge.js needs no modification — it believes it is inside WebView2.
 * Mirrors TiroFormViewer: ack the handshake, then sdc.configure -> displayQuestionnaire,
 * and record inbound notifications for the test to assert on.
 */
export const HOST_SHIM = `(() => {
  const state = {
    handshakes: [],      // page->host status.handshake payloads (GH-61 version report)
    submitted: [],       // form.submitted payloads
    dirtyChanges: [],    // ui.form.dirtyChanged payloads
    done: [],            // ui.done
    outbound: [],        // everything the page sent, in order
    errors: [],
  };
  window.__host = state;

  const listeners = [];
  const send = envelope => listeners.forEach(cb => cb({ data: envelope }));
  const respond = (toId, payload = { $type: "base" }) =>
    send({ messageId: "host-" + Math.random().toString(36).slice(2), responseToMessageId: toId,
           additionalResponsesExpected: false, payload });

  window.chrome = {
    webview: {
      postMessage(msg) {
        state.outbound.push(msg);
        switch (msg.messageType) {
          case "status.handshake":
            state.handshakes.push(msg.payload);
            respond(msg.messageId);
            break;
          case "form.submitted":
            state.submitted.push(msg.payload);
            respond(msg.messageId);
            break;
          case "ui.form.dirtyChanged":
            state.dirtyChanges.push(msg.payload);
            break;
          case "ui.done":
            state.done.push(msg.payload);
            break;
          default:
            if (msg.responseToMessageId) {
              // The page acking one of our requests; surface page-side errors.
              if (msg.payload && msg.payload.$type === "error") state.errors.push(msg.payload);
            }
        }
      },
      addEventListener(type, cb) { if (type === "message") listeners.push(cb); },
    },
  };

  // Host->page requests, as TiroFormViewer.SetContextAsync sends them.
  window.__hostSend = (messageType, payload) => {
    const messageId = "host-req-" + Math.random().toString(36).slice(2);
    send({ messageId, messagingHandle: "smart-web-messaging", messageType, payload });
    return messageId;
  };
})();`;
