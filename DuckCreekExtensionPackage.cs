using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Task = System.Threading.Tasks.Task;
using System.Linq;

namespace DuckCreekExtension
{
    // Define the adornment layer FIRST
    [Export(typeof(AdornmentLayerDefinition))]
    [Name("DuckCreekGhostTextLayer")]
    [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Caret)]
    internal sealed class DuckCreekGhostTextLayerDefinition
    {
        // Empty class - just defines the layer
    }

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
        private Dictionary<IWpfTextView, SimpleGhostTextManager> _ghostManagers = new Dictionary<IWpfTextView, SimpleGhostTextManager>();

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
                    await System.Threading.Tasks.Task.Delay(200);

                    if (pFrame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out object docView) == VSConstants.S_OK)
                    {
                        if (docView is IVsCodeWindow codeWindow &&
                            codeWindow.GetPrimaryView(out IVsTextView primaryView) == VSConstants.S_OK &&
                            primaryView != null)
                        {
                            var wpfTextView = _editorAdapterFactory.GetWpfTextView(primaryView);
                            if (wpfTextView != null)
                            {
                                System.Diagnostics.Debug.WriteLine("=== DuckCreek: Setting up simple ghost text monitoring ===");
                                SetupSimpleGhostTextMonitoring(wpfTextView);
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

        private void SetupSimpleGhostTextMonitoring(IWpfTextView textView)
        {
            try
            {
                // Use a simpler approach without adornment layers
                var ghostManager = new SimpleGhostTextManager(textView);
                _ghostManagers[textView] = ghostManager;

                var textBuffer = textView.TextBuffer;

                textBuffer.Changed += (sender, e) =>
                {
                    try
                    {
                        bool hasNewline = false;
                        foreach (var change in e.Changes)
                        {
                            if (change.NewText.Contains("\r") || change.NewText.Contains("\n"))
                            {
                                hasNewline = true;
                                break;
                            }
                        }

                        if (!hasNewline) return;

                        System.Diagnostics.Debug.WriteLine("=== DuckCreek: Enter detected in text changes ===");

                        var now = DateTime.Now;
                        if (_lastProcessedTime.ContainsKey(textBuffer))
                        {
                            var timeSinceLastProcess = now - _lastProcessedTime[textBuffer];
                            if (timeSinceLastProcess.TotalMilliseconds < 1000)
                            {
                                System.Diagnostics.Debug.WriteLine("=== DuckCreek: Throttled - too soon since last process ===");
                                return;
                            }
                        }

                        _lastProcessedTime[textBuffer] = now;

                        ThreadHelper.JoinableTaskFactory.Run(async () =>
                        {
                            await System.Threading.Tasks.Task.Delay(50);
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                            CheckForTriggerPhrase(textView, textBuffer, ghostManager);
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in text buffer changed: {ex.Message} ===");
                    }
                };

                System.Diagnostics.Debug.WriteLine("=== DuckCreek: Simple ghost text monitoring setup complete ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in SetupSimpleGhostTextMonitoring: {ex.Message} ===");
            }
        }

        private void CheckForTriggerPhrase(IWpfTextView textView, ITextBuffer textBuffer, SimpleGhostTextManager ghostManager)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var snapshot = textBuffer.CurrentSnapshot;
                var caretPosition = textView.Caret.Position.BufferPosition;
                var currentLineNumber = snapshot.GetLineNumberFromPosition(caretPosition);

                System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Checking at line {currentLineNumber} ===");

                var linesToCheck = new List<int>();
                if (currentLineNumber > 0) linesToCheck.Add(currentLineNumber - 1);
                linesToCheck.Add(currentLineNumber);
                if (currentLineNumber > 1) linesToCheck.Add(currentLineNumber - 2);

                foreach (var lineNumber in linesToCheck)
                {
                    if (lineNumber < 0 || lineNumber >= snapshot.LineCount) continue;

                    var line = snapshot.GetLineFromLineNumber(lineNumber);
                    var lineText = line.GetText().Trim();

                    System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Checking line {lineNumber}: '{lineText}' ===");

                    // Check for trigger and get response entity
                    string responseEntity = GetTriggerResponse(lineText);

                    if (!string.IsNullOrEmpty(responseEntity))
                    {
                        System.Diagnostics.Debug.WriteLine($"=== DuckCreek: TRIGGER FOUND! Response entity: '{responseEntity}' ===");

                        string fileName = "Unknown File";
                        string fileExtension = "";
                        string fileContent = "";

                        if (textBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDocument))
                        {
                            fileName = Path.GetFileName(textDocument.FilePath) ?? "Unknown File";
                            fileExtension = Path.GetExtension(textDocument.FilePath)?.ToLower() ?? "";
                        }

                        // Read file content
                        try
                        {
                            if (!string.IsNullOrEmpty(textDocument.FilePath) && File.Exists(textDocument.FilePath))
                            {
                                fileContent = File.ReadAllText(textDocument.FilePath);
                                System.Diagnostics.Debug.WriteLine($"=== DuckCreek file content: {fileContent} ===");
                            }
                            else
                            {
                                var snapshot1 = textBuffer.CurrentSnapshot;
                                fileContent = snapshot1.GetText();
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR reading file content: {ex.Message} ===");
                            fileContent = "";
                        }

                        string commentPrefix = GetCommentPrefix(fileExtension);
                        string ghostMessage = $"{commentPrefix} Hello From {responseEntity}! You are in the file {fileName}";

                        System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Generated message: {ghostMessage} ===");

                        ghostManager.ShowGhostText(line, ghostMessage);
                        return;
                    }
                }

                System.Diagnostics.Debug.WriteLine("=== DuckCreek: No trigger phrase found in checked lines ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in CheckForTriggerPhrase: {ex.Message} ===");
            }
        }

        // Helper method to check triggers and return response entity
        private string GetTriggerResponse(string lineText)
        {
            if (string.IsNullOrEmpty(lineText)) return "";

            // Define trigger-response pairs
            var triggers = new Dictionary<string, string>
    {
        { "Hi Duck Creek", "Duck Creek" },
        { "Hi Server", "Server" },
        { "Hi API", "API" },
        { "Hi Database", "Database" },
        { "Hi Bot", "Bot" }
        // Add more triggers as needed
    };

            foreach (var trigger in triggers)
            {
                if (lineText.IndexOf(trigger.Key, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !lineText.Contains($"Hello From {trigger.Value}"))
                {
                    return trigger.Value;
                }
            }

            return ""; // No trigger found
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

    // Simplified ghost text manager that doesn't use adornment layers
   public class SimpleGhostTextManager
{
    private readonly IWpfTextView _textView;
    private string _pendingGhostText;
    private ITextSnapshotLine _triggerLine;
    private bool _isShowingGhost = false;

    public SimpleGhostTextManager(IWpfTextView textView)
    {
        _textView = textView;
        
        // Subscribe to events
        _textView.VisualElement.KeyDown += OnKeyDown;
        _textView.TextBuffer.Changed += OnTextChanged;
        
        // Don't subscribe to caret moved initially - it hides ghost text too quickly
    }

    public void ShowGhostText(ITextSnapshotLine triggerLine, string ghostText)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Showing ghost text: {ghostText} ===");

            _pendingGhostText = ghostText;
            _triggerLine = triggerLine;
            _isShowingGhost = true;

            // Show in status bar (reliable method)
            ShowInStatusBar(ghostText);

            // Also try to insert temporary ghost text directly in the editor
            InsertTemporaryGhostText(ghostText);

            System.Diagnostics.Debug.WriteLine("=== DuckCreek: Ghost text displayed ===");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in ShowGhostText: {ex.Message} ===");
        }
    }

    private void InsertTemporaryGhostText(string ghostText)
    {
        try
        {
            // Insert the ghost text as actual text but mark it as temporary
            using (var edit = _textView.TextBuffer.CreateEdit())
            {
                var insertText = Environment.NewLine + $"// 👻 {ghostText} (Press Tab to keep, any key to dismiss)";
                edit.Insert(_triggerLine.End, insertText);
                edit.Apply();
            }

            System.Diagnostics.Debug.WriteLine("=== DuckCreek: Temporary ghost text inserted ===");

            // Subscribe to caret moved AFTER inserting ghost text
            _textView.Caret.PositionChanged += OnCaretMoved;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in InsertTemporaryGhostText: {ex.Message} ===");
        }
    }

    private void ShowInStatusBar(string ghostText)
    {
        try
        {
            var statusBar = Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;
            if (statusBar != null)
            {
                statusBar.SetText($"👻 GHOST: {ghostText} (Press Tab to accept)");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in ShowInStatusBar: {ex.Message} ===");
        }
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        try
        {
            if (!_isShowingGhost) return;

            System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Key pressed while ghost showing: {e.Key} ===");
            
            if (e.Key == System.Windows.Input.Key.Tab && !string.IsNullOrEmpty(_pendingGhostText))
            {
                System.Diagnostics.Debug.WriteLine("=== DuckCreek: Tab detected - accepting ghost text ===");
                e.Handled = true;
                AcceptGhostText();
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                System.Diagnostics.Debug.WriteLine("=== DuckCreek: Escape pressed - dismissing ghost text ===");
                DismissGhostText();
            }
            else if (e.Key != System.Windows.Input.Key.Up &&
                     e.Key != System.Windows.Input.Key.Down &&
                     e.Key != System.Windows.Input.Key.Left &&
                     e.Key != System.Windows.Input.Key.Right)
            {
                System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Other key pressed ({e.Key}) - dismissing ghost text ===");
                DismissGhostText();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in OnKeyDown: {ex.Message} ===");
        }
    }

    private void OnTextChanged(object sender, TextContentChangedEventArgs e)
    {
        try
        {
            if (!_isShowingGhost) return;

            foreach (var change in e.Changes)
            {
                System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Text change while ghost showing: '{change.NewText}' ===");
                
                // Check for Tab character
                if (change.NewText == "\t" || 
                    (change.NewText.Length >= 2 && change.NewText.All(c => c == ' ')))
                {
                    System.Diagnostics.Debug.WriteLine("=== DuckCreek: Tab character detected - accepting ghost text ===");
                    
                    // Remove the tab
                    using (var edit = _textView.TextBuffer.CreateEdit())
                    {
                        edit.Delete(change.NewSpan);
                        edit.Apply();
                    }
                    
                    AcceptGhostText();
                    return;
                }
                
                // Dismiss ghost text for other typing (except arrow keys handled in KeyDown)
                if (!string.IsNullOrEmpty(change.NewText) && 
                    !change.NewText.Contains("\r") && 
                    !change.NewText.Contains("\n") &&
                    change.NewText != "\t" &&
                    !change.NewText.Contains("👻")) // Don't dismiss when we insert our own ghost text
                {
                    System.Diagnostics.Debug.WriteLine("=== DuckCreek: User typed something - dismissing ghost text ===");
                    DismissGhostText();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in OnTextChanged: {ex.Message} ===");
        }
    }

    private void AcceptGhostText()
    {
        try
        {
            if (string.IsNullOrEmpty(_pendingGhostText) || _triggerLine == null) return;

            System.Diagnostics.Debug.WriteLine("=== DuckCreek: Accepting ghost text ===");

            // Remove the temporary ghost text first
            RemoveTemporaryGhostText();

            // Insert the real ghost text
            using (var edit = _textView.TextBuffer.CreateEdit())
            {
                edit.Insert(_triggerLine.End, Environment.NewLine + _pendingGhostText);
                edit.Apply();
            }

            HideGhostText();
            System.Diagnostics.Debug.WriteLine("=== DuckCreek: Ghost text accepted and inserted ===");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in AcceptGhostText: {ex.Message} ===");
        }
    }

    private void DismissGhostText()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("=== DuckCreek: Dismissing ghost text ===");
            
            // Remove the temporary ghost text
            RemoveTemporaryGhostText();
            
            HideGhostText();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in DismissGhostText: {ex.Message} ===");
        }
    }

    private void RemoveTemporaryGhostText()
    {
        try
        {
            var snapshot = _textView.TextBuffer.CurrentSnapshot;
            
            // Find and remove lines containing our ghost text marker
            for (int i = 0; i < snapshot.LineCount; i++)
            {
                var line = snapshot.GetLineFromLineNumber(i);
                var lineText = line.GetText();
                
                if (lineText.Contains("// 👻") && lineText.Contains("(Press Tab to keep"))
                {
                    using (var edit = _textView.TextBuffer.CreateEdit())
                    {
                        // Delete the entire line including the newline
                        var lineSpan = new Span(line.Start.Position, line.LengthIncludingLineBreak);
                        edit.Delete(lineSpan);
                        edit.Apply();
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in RemoveTemporaryGhostText: {ex.Message} ===");
        }
    }

    private void HideGhostText()
    {
        try
        {
            _isShowingGhost = false;
            _pendingGhostText = null;
            _triggerLine = null;
            
            // Unsubscribe from caret moved
            _textView.Caret.PositionChanged -= OnCaretMoved;
            
            // Clear status bar
            var statusBar = Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;
            statusBar?.Clear();
            
            System.Diagnostics.Debug.WriteLine("=== DuckCreek: Ghost text hidden ===");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in HideGhostText: {ex.Message} ===");
        }
    }

    private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e)
    {
        // Only dismiss if user moves cursor significantly
        if (_isShowingGhost)
        {
            System.Diagnostics.Debug.WriteLine("=== DuckCreek: Caret moved - dismissing ghost text ===");
            DismissGhostText();
        }
    }
}

}
