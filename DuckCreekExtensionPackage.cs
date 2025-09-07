using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace DuckCreekExtension
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(DuckCreekExtensionPackage.PackageGuidString)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class DuckCreekExtensionPackage : AsyncPackage, IVsRunningDocTableEvents
    {
        public const string PackageGuidString = "50875327-b916-4c3f-b5c1-cee1cbcd6650";
        private uint _rdtCookie;
        private IVsRunningDocumentTable _rdt;
        private IVsEditorAdaptersFactoryService _editorAdapterFactory;
        private Dictionary<ITextBuffer, DateTime> _lastProcessedTime = new Dictionary<ITextBuffer, DateTime>();

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            System.Diagnostics.Debug.WriteLine("=== DuckCreek: Package InitializeAsync called ===");

            try
            {
                var componentModel = await GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
                if (componentModel != null)
                {
                    _editorAdapterFactory = componentModel.GetService<IVsEditorAdaptersFactoryService>();
                    System.Diagnostics.Debug.WriteLine("=== DuckCreek: Got editor adapter factory ===");
                }

                _rdt = await GetServiceAsync(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
                if (_rdt != null)
                {
                    System.Diagnostics.Debug.WriteLine("=== DuckCreek: Got Running Document Table ===");
                    _rdt.AdviseRunningDocTableEvents(this, out _rdtCookie);
                    System.Diagnostics.Debug.WriteLine("=== DuckCreek: Subscribed to document events ===");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR: {ex.Message} ===");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_rdt != null && _rdtCookie != 0)
            {
                _rdt.UnadviseRunningDocTableEvents(_rdtCookie);
            }
            base.Dispose(disposing);
        }

        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        public int OnAfterSave(uint docCookie) => VSConstants.S_OK;
        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;

        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
        {
            System.Diagnostics.Debug.WriteLine("=== DuckCreek: Document window showing ===");

            try
            {
                if (_editorAdapterFactory == null) return VSConstants.S_OK;

                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await System.Threading.Tasks.Task.Delay(200); // Wait for view to be ready

                    if (pFrame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out object docView) == VSConstants.S_OK)
                    {
                        if (docView is IVsCodeWindow codeWindow &&
                            codeWindow.GetPrimaryView(out IVsTextView primaryView) == VSConstants.S_OK &&
                            primaryView != null)
                        {
                            var wpfTextView = _editorAdapterFactory.GetWpfTextView(primaryView);
                            if (wpfTextView != null)
                            {
                                System.Diagnostics.Debug.WriteLine("=== DuckCreek: Setting up text buffer monitoring ===");
                                SetupTextBufferMonitoring(wpfTextView);
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR: {ex.Message} ===");
            }

            return VSConstants.S_OK;
        }

        private void SetupTextBufferMonitoring(IWpfTextView textView)
        {
            try
            {
                var textBuffer = textView.TextBuffer;

                // Subscribe to text buffer changes with throttling
                textBuffer.Changed += (sender, e) =>
                {
                    try
                    {
                        // Only process if Enter key was pressed (newline in changes)
                        bool hasNewline = false;
                        foreach (var change in e.Changes)
                        {
                            if (change.NewText.Contains("\r") || change.NewText.Contains("\n"))
                            {
                                hasNewline = true;
                                break;
                            }
                        }

                        if (!hasNewline)
                        {
                            return; // Not an Enter key press
                        }

                        System.Diagnostics.Debug.WriteLine("=== DuckCreek: Enter detected in text changes ===");

                        // Throttle: only process if enough time has passed since last processing
                        var now = DateTime.Now;
                        if (_lastProcessedTime.ContainsKey(textBuffer))
                        {
                            var timeSinceLastProcess = now - _lastProcessedTime[textBuffer];
                            if (timeSinceLastProcess.TotalMilliseconds < 1000) // 1 second throttle
                            {
                                System.Diagnostics.Debug.WriteLine("=== DuckCreek: Throttled - too soon since last process ===");
                                return;
                            }
                        }

                        _lastProcessedTime[textBuffer] = now;

                        // Use JoinableTaskFactory to ensure we're on the UI thread
                        ThreadHelper.JoinableTaskFactory.Run(async () =>
                        {
                            await System.Threading.Tasks.Task.Delay(50);
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            CheckForTriggerPhrase(textView, textBuffer);
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in text buffer changed: {ex.Message} ===");
                    }
                };

                System.Diagnostics.Debug.WriteLine("=== DuckCreek: Text buffer monitoring setup complete ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in SetupTextBufferMonitoring: {ex.Message} ===");
            }
        }

        private void CheckForTriggerPhrase(IWpfTextView textView, ITextBuffer textBuffer)
        {
            try
            {
                // Ensure we're on the UI thread
                ThreadHelper.ThrowIfNotOnUIThread();

                var snapshot = textBuffer.CurrentSnapshot;
                var caretPosition = textView.Caret.Position.BufferPosition;
                var currentLineNumber = snapshot.GetLineNumberFromPosition(caretPosition);

                System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Checking at line {currentLineNumber} ===");

                // Check multiple lines around the cursor to find "Hi Duck Creek"
                var linesToCheck = new List<int>();

                // Add lines to check (current line and previous lines)
                if (currentLineNumber > 0) linesToCheck.Add(currentLineNumber - 1); // Previous line
                linesToCheck.Add(currentLineNumber); // Current line
                if (currentLineNumber > 1) linesToCheck.Add(currentLineNumber - 2); // Line before previous

                foreach (var lineNumber in linesToCheck)
                {
                    if (lineNumber < 0 || lineNumber >= snapshot.LineCount) continue;

                    var line = snapshot.GetLineFromLineNumber(lineNumber);
                    var lineText = line.GetText().Trim();

                    System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Checking line {lineNumber}: '{lineText}' ===");

                    if (!string.IsNullOrEmpty(lineText) &&
                        lineText.IndexOf("Hi Duck Creek", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        !lineText.Contains("Hello From Duck Creek"))
                    {
                        System.Diagnostics.Debug.WriteLine("=== DuckCreek: TRIGGER FOUND! ===");

                        // Check if response already exists on the next line
                        bool responseExists = false;
                        if (lineNumber + 1 < snapshot.LineCount)
                        {
                            var nextLine = snapshot.GetLineFromLineNumber(lineNumber + 1);
                            var nextLineText = nextLine.GetText().Trim();
                            if (nextLineText.Contains("Hello From Duck Creek"))
                            {
                                System.Diagnostics.Debug.WriteLine("=== DuckCreek: Response already exists on next line ===");
                                responseExists = true;
                            }
                        }

                        if (!responseExists)
                        {
                            InsertResponse(textBuffer, line);
                            return; // Exit after processing one trigger
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine("=== DuckCreek: No trigger phrase found in checked lines ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in CheckForTriggerPhrase: {ex.Message} ===");
            }
        }

        private void InsertResponse(ITextBuffer textBuffer, ITextSnapshotLine triggerLine)
        {
            try
            {
                // Ensure we're on the UI thread
                ThreadHelper.ThrowIfNotOnUIThread();

                System.Diagnostics.Debug.WriteLine("=== DuckCreek: InsertResponse called ===");

                string fileName = "Unknown File";
                string fileExtension = "";

                if (textBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDocument))
                {
                    fileName = Path.GetFileName(textDocument.FilePath) ?? "Unknown File";
                    fileExtension = Path.GetExtension(textDocument.FilePath)?.ToLower() ?? "";
                }

                string commentPrefix = GetCommentPrefix(fileExtension);
                string responseMessage = $"{commentPrefix} Hello From Duck Creek! You are in the file {fileName}";

                System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Inserting: '{responseMessage}' ===");

                using (var edit = textBuffer.CreateEdit())
                {
                    edit.Insert(triggerLine.End, Environment.NewLine + responseMessage);
                    edit.Apply();
                }

                System.Diagnostics.Debug.WriteLine("=== DuckCreek: Response inserted successfully ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in InsertResponse: {ex.Message} ===");
            }
        }

        private string GetCommentPrefix(string fileExtension)
        {
            switch (fileExtension)
            {
                case ".cs":
                case ".js":
                case ".ts":
                case ".cpp":
                case ".c":
                case ".h":
                case ".java":
                case ".json":
                    return "//";
                case ".xml":
                case ".html":
                case ".htm":
                    return "<!--";
                case ".py":
                case ".ps1":
                case ".sh":
                    return "#";
                case ".sql":
                    return "--";
                case ".vb":
                    return "'";
                default:
                    return "//";
            }
        }
    }
}
