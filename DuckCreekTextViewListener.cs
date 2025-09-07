using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.ComponentModel.Composition;
using System.IO;

namespace DuckCreekExtension
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Interactive)]
    public class DuckCreekTextViewListener : IWpfTextViewCreationListener
    {
        public void TextViewCreated(IWpfTextView textView)
        {
            System.Diagnostics.Debug.WriteLine("=== DuckCreek: TextViewCreated called ===");

            try
            {
                // Subscribe to text buffer changes
                textView.TextBuffer.Changed += (sender, e) => OnTextBufferChanged(sender, e);
                System.Diagnostics.Debug.WriteLine("=== DuckCreek: Successfully subscribed to TextBuffer.Changed ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in TextViewCreated: {ex.Message} ===");
            }
        }

        private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("=== DuckCreek: OnTextBufferChanged called ===");

                var textBuffer = sender as ITextBuffer;
                if (textBuffer == null)
                {
                    System.Diagnostics.Debug.WriteLine("=== DuckCreek: textBuffer is null ===");
                    return;
                }

                if (e.Changes.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("=== DuckCreek: No changes detected ===");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"=== DuckCreek: {e.Changes.Count} changes detected ===");

                // Check if any of the changes contain our trigger phrase
                foreach (var change in e.Changes)
                {
                    var newText = change.NewText;
                    System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Change text: '{newText}' ===");

                    if (newText.IndexOf("Hi Duck Creek", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        System.Diagnostics.Debug.WriteLine("=== DuckCreek: TRIGGER PHRASE FOUND in change! ===");

                        // Wait a moment for the text to be fully applied, then check the line
                        System.Threading.Tasks.Task.Delay(100).ContinueWith(_ =>
                        {
                            CheckForTriggerPhrase(textBuffer, e.After);
                        });
                        break;
                    }
                }

                // Also check the current line where changes occurred
                CheckForTriggerPhrase(textBuffer, e.After);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== DuckCreek ERROR in OnTextBufferChanged: {ex.Message} ===");
            }
        }

        private void CheckForTriggerPhrase(ITextBuffer textBuffer, ITextSnapshot snapshot)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("=== DuckCreek: CheckForTriggerPhrase called ===");

                var lineCount = snapshot.LineCount;
                System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Total lines: {lineCount} ===");

                // Check the last few lines
                var linesToCheck = Math.Min(5, lineCount);
                for (int i = lineCount - linesToCheck; i < lineCount; i++)
                {
                    if (i < 0) continue;

                    var line = snapshot.GetLineFromLineNumber(i);
                    var lineText = line.GetText().Trim();

                    System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Line {i}: '{lineText}' ===");

                    if (lineText.IndexOf("Hi Duck Creek", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        !lineText.Contains("Hello From Duck Creek"))
                    {
                        System.Diagnostics.Debug.WriteLine("=== DuckCreek: TRIGGER PHRASE FOUND! ===");

                        // Check if response already exists on next line
                        bool responseExists = false;
                        if (i + 1 < lineCount)
                        {
                            var nextLine = snapshot.GetLineFromLineNumber(i + 1);
                            var nextLineText = nextLine.GetText().Trim();
                            if (nextLineText.Contains("Hello From Duck Creek"))
                            {
                                System.Diagnostics.Debug.WriteLine("=== DuckCreek: Response already exists, skipping ===");
                                responseExists = true;
                            }
                        }

                        if (!responseExists)
                        {
                            InsertResponse(textBuffer, line);
                        }
                        break;
                    }
                }
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
                System.Diagnostics.Debug.WriteLine("=== DuckCreek: InsertResponse called ===");

                string fileName = "Unknown File";
                string fileExtension = "";

                if (textBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument textDocument))
                {
                    fileName = Path.GetFileName(textDocument.FilePath) ?? "Unknown File";
                    fileExtension = Path.GetExtension(textDocument.FilePath)?.ToLower() ?? "";
                    System.Diagnostics.Debug.WriteLine($"=== DuckCreek: File: {fileName}, Extension: {fileExtension} ===");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("=== DuckCreek: Could not get file information ===");
                }

                string commentPrefix = GetCommentPrefix(fileExtension);
                string responseMessage = $"{commentPrefix} Hello From Duck Creek! You are in the file {fileName}";

                System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Response message: '{responseMessage}' ===");

                using (var edit = textBuffer.CreateEdit())
                {
                    edit.Insert(triggerLine.End, Environment.NewLine + responseMessage);
                    var result = edit.Apply();
                    System.Diagnostics.Debug.WriteLine($"=== DuckCreek: Edit applied successfully: {result} ===");
                }
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