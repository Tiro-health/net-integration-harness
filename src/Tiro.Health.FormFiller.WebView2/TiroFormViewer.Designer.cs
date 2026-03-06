namespace Tiro.Health.FormFiller.WebView2
{
    partial class TiroFormViewer
    {
        private System.ComponentModel.IContainer components = null;

        [System.Diagnostics.DebuggerNonUserCode]
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    // 1. Unhook WebView2 events
                    if (WebView2Host != null && WebView2Host.CoreWebView2 != null)
                    {
                        WebView2Host.CoreWebView2.WebMessageReceived -= SMARTWebMessageReceived;
                        WebView2Host.CoreWebView2.PermissionRequested -= OnPermissionRequested;
                    }

                    // 2. Unhook Message Handler events (CRITICAL for memory management)
                    if (_smartWebMessageHandler != null)
                    {
                        _smartWebMessageHandler.FormSubmitted -= OnFormSubmitted;
                        _smartWebMessageHandler.HandshakeReceived -= OnHandshakeReceived;
                    }

                    // 3. Cleanup the Sentry transaction
                    FinishSentryTransaction();

                    // 4. Dispose child components
                    if (components != null)
                        components.Dispose();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        [System.Diagnostics.DebuggerStepThrough]
        private void InitializeComponent()
        {
            this.WebView2Host = new Microsoft.Web.WebView2.WinForms.WebView2();
            ((System.ComponentModel.ISupportInitialize)(this.WebView2Host)).BeginInit();
            this.SuspendLayout();
            //
            // WebView2Host
            //
            this.WebView2Host.AllowExternalDrop = true;
            this.WebView2Host.CreationProperties = null;
            this.WebView2Host.DefaultBackgroundColor = System.Drawing.Color.White;
            this.WebView2Host.Dock = System.Windows.Forms.DockStyle.Fill;
            this.WebView2Host.Location = new System.Drawing.Point(0, 0);
            this.WebView2Host.Name = "WebView2Host";
            this.WebView2Host.Size = new System.Drawing.Size(800, 450);
            this.WebView2Host.TabIndex = 0;
            this.WebView2Host.ZoomFactor = 1D;
            //
            // TiroFormViewer
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.WebView2Host);
            this.Name = "TiroFormViewer";
            this.Size = new System.Drawing.Size(800, 450);
            ((System.ComponentModel.ISupportInitialize)(this.WebView2Host)).EndInit();
            this.ResumeLayout(false);
        }

        private Microsoft.Web.WebView2.WinForms.WebView2 WebView2Host;
    }
}
