using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Search;

namespace BrainX.Client.Editor;

public partial class MarkdownEditor
{
    private readonly TextEditor _editor;
    private readonly FlowDocumentScrollViewer _preview;
    private DispatcherTimer _autoSaveTimer = null!;
    private readonly DispatcherTimer _previewTimer;
    private string? _currentFilePath;
    private bool _isDirty;
    private bool _isLoading;
    private string _vaultPath;

    public event Action<string>? WikiLinkClicked;
    public event Action<string>? FileSaved;
    public event Action<bool>? DirtyStateChanged;

    public string? CurrentFilePath => _currentFilePath;
    public bool IsDirty => _isDirty;

    public MarkdownEditor(TextEditor editor, FlowDocumentScrollViewer preview, string vaultPath)
    {
        _editor = editor;
        _preview = preview;
        _vaultPath = vaultPath;

        SetupEditor();
        SetupAutoSave();
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RenderPreview(); };
    }

    public void UpdateVaultPath(string path) => _vaultPath = path;

    private void SetupEditor()
    {
        // Load markdown syntax highlighting
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("MarkdownHighlighting.xshd"));
        if (resourceName != null)
        {
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new XmlTextReader(stream);
                _editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
        }

        // Editor appearance
        _editor.FontFamily = new FontFamily("JetBrains Mono, Cascadia Code, Consolas");
        _editor.FontSize = 14;
        _editor.Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x1A));
        _editor.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0));
        _editor.LineNumbersForeground = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x6A));
        _editor.ShowLineNumbers = true;
        _editor.WordWrap = true;
        _editor.Padding = new Thickness(8);

        // Enable search panel (Ctrl+F)
        SearchPanel.Install(_editor);

        // Track changes
        _editor.TextChanged += (_, _) =>
        {
            if (_isLoading) return;
            if (!_isDirty) { _isDirty = true; DirtyStateChanged?.Invoke(true); }
            _previewTimer.Stop();
            _previewTimer.Start();
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        };

        // Wiki-link click: Ctrl+Click
        _editor.PreviewMouseLeftButtonDown += OnEditorMouseDown;

        // Keyboard shortcuts
        _editor.InputBindings.Add(new KeyBinding(
            new RelayCommand(() => Save()), Key.S, ModifierKeys.Control));
        _editor.InputBindings.Add(new KeyBinding(
            new RelayCommand(() => InsertWikiLink()), Key.K, ModifierKeys.Control));
        _editor.InputBindings.Add(new KeyBinding(
            new RelayCommand(() => ToggleBold()), Key.B, ModifierKeys.Control));
        _editor.InputBindings.Add(new KeyBinding(
            new RelayCommand(() => ToggleItalic()), Key.I, ModifierKeys.Control));
    }

    private void SetupAutoSave()
    {
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); Save(); };
    }

    // ═══════════════════════════════════════
    // FILE OPERATIONS
    // ═══════════════════════════════════════

    public void OpenFile(string filePath)
    {
        if (_isDirty) Save(); // Auto-save previous
        _isLoading = true;
        try
        {
            _currentFilePath = filePath;
            _editor.Text = File.Exists(filePath) ? File.ReadAllText(filePath) : "";
            _isDirty = false;
            DirtyStateChanged?.Invoke(false);
            RenderPreview();
            _editor.ScrollToHome();
        }
        finally { _isLoading = false; }
    }

    public void NewFile(string filePath, string template = "")
    {
        var dir = System.IO.Path.GetDirectoryName(filePath);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        if (string.IsNullOrEmpty(template))
        {
            var title = System.IO.Path.GetFileNameWithoutExtension(filePath);
            template = $"---\ntitle: {title}\ndate: {DateTime.Now:yyyy-MM-dd}\ntags: []\n---\n\n# {title}\n\n";
        }
        File.WriteAllText(filePath, template);
        OpenFile(filePath);
    }

    public void Save()
    {
        if (_currentFilePath == null || !_isDirty) return;
        var dir = System.IO.Path.GetDirectoryName(_currentFilePath);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_currentFilePath, _editor.Text);
        _isDirty = false;
        DirtyStateChanged?.Invoke(false);
        FileSaved?.Invoke(_currentFilePath);
    }

    // ═══════════════════════════════════════
    // PREVIEW RENDERING
    // ═══════════════════════════════════════

    private void RenderPreview()
    {
        var doc = new FlowDocument
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0)),
            FontFamily = new FontFamily("Segoe UI, sans-serif"),
            FontSize = 14,
            PagePadding = new Thickness(16)
        };

        var text = _editor.Text;
        var lines = text.Split('\n');
        bool inCodeBlock = false;
        bool inYaml = false;
        var codeLines = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            // YAML frontmatter
            if (i == 0 && line == "---") { inYaml = true; continue; }
            if (inYaml)
            {
                if (line == "---") { inYaml = false; continue; }
                continue; // Skip YAML in preview
            }

            // Code blocks
            if (line.StartsWith("```"))
            {
                if (inCodeBlock)
                {
                    var codeBlock = new Paragraph
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x28)),
                        Padding = new Thickness(12),
                        FontFamily = new FontFamily("JetBrains Mono, Consolas"),
                        FontSize = 12.5,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x4A)),
                        BorderThickness = new Thickness(1),
                        Margin = new Thickness(0, 4, 0, 4)
                    };
                    codeBlock.Inlines.Add(new Run(string.Join("\n", codeLines)));
                    doc.Blocks.Add(codeBlock);
                    codeLines.Clear();
                    inCodeBlock = false;
                }
                else { inCodeBlock = true; }
                continue;
            }
            if (inCodeBlock) { codeLines.Add(line); continue; }

            // Empty line
            if (string.IsNullOrWhiteSpace(line))
            {
                doc.Blocks.Add(new Paragraph(new Run(" ")) { FontSize = 6, Margin = new Thickness(0) });
                continue;
            }

            // Horizontal rule
            if (Regex.IsMatch(line, @"^(\*\*\*|---|___)$"))
            {
                var hr = new Paragraph(new Run(""))
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x4A)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 8, 0, 8)
                };
                doc.Blocks.Add(hr);
                continue;
            }

            // Headings
            if (line.StartsWith("# "))
            {
                doc.Blocks.Add(MakeHeading(line[2..], 22, Color.FromRgb(0, 240, 255)));
                continue;
            }
            if (line.StartsWith("## "))
            {
                doc.Blocks.Add(MakeHeading(line[3..], 19, Color.FromRgb(139, 92, 246)));
                continue;
            }
            if (line.StartsWith("### "))
            {
                doc.Blocks.Add(MakeHeading(line[4..], 16, Color.FromRgb(255, 0, 110)));
                continue;
            }
            if (line.StartsWith("#### "))
            {
                doc.Blocks.Add(MakeHeading(line[5..], 14.5, Color.FromRgb(0, 255, 136)));
                continue;
            }

            // Block quote
            if (line.StartsWith("> "))
            {
                var quote = new Paragraph
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(139, 92, 246)),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 4, 4, 4),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xBA)),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 4, 0, 4)
                };
                AddInlines(quote.Inlines, line[2..]);
                doc.Blocks.Add(quote);
                continue;
            }

            // Task list
            var taskMatch = Regex.Match(line, @"^\s*-\s\[([ x])\]\s(.*)");
            if (taskMatch.Success)
            {
                bool done = taskMatch.Groups[1].Value == "x";
                var p = new Paragraph { Margin = new Thickness(16, 2, 0, 2) };
                var checkbox = done ? "\u2611 " : "\u2610 ";
                var run = new Run(checkbox + taskMatch.Groups[2].Value);
                if (done)
                {
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 136));
                    run.TextDecorations = TextDecorations.Strikethrough;
                }
                else
                    run.Foreground = new SolidColorBrush(Color.FromRgb(255, 0, 110));
                p.Inlines.Add(run);
                doc.Blocks.Add(p);
                continue;
            }

            // List items
            var listMatch = Regex.Match(line, @"^(\s*)([-*+]|\d+\.)\s(.*)");
            if (listMatch.Success)
            {
                int indent = listMatch.Groups[1].Value.Length;
                var marker = listMatch.Groups[2].Value;
                var content = listMatch.Groups[3].Value;
                var p = new Paragraph { Margin = new Thickness(16 + indent * 12, 2, 0, 2) };
                var markerRun = new Run(Regex.IsMatch(marker, @"\d+\.") ? marker + " " : "\u2022 ")
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 136)),
                    FontWeight = FontWeights.Bold
                };
                p.Inlines.Add(markerRun);
                AddInlines(p.Inlines, content);
                doc.Blocks.Add(p);
                continue;
            }

            // Regular paragraph
            var para = new Paragraph { Margin = new Thickness(0, 3, 0, 3), LineHeight = 22 };
            AddInlines(para.Inlines, line);
            doc.Blocks.Add(para);
        }

        _preview.Document = doc;
    }

    private static Paragraph MakeHeading(string text, double fontSize, Color color)
    {
        var p = new Paragraph
        {
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(color),
            Margin = new Thickness(0, 12, 0, 6),
            BorderBrush = fontSize >= 19
                ? new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B))
                : null,
            BorderThickness = fontSize >= 19 ? new Thickness(0, 0, 0, 1) : new Thickness(0)
        };
        p.Inlines.Add(new Run(text));
        return p;
    }

    private void AddInlines(InlineCollection inlines, string text)
    {
        // Process: wiki-links, bold, italic, inline code, strikethrough, links, tags
        var pattern = @"(\[\[[^\]]+\]\])|(\*\*[^*]+\*\*)|(\*[^*]+\*)|(`[^`]+`)|(~~[^~]+~~)|(\[[^\]]+\]\([^\)]+\))|(#[a-zA-Z0-9_/\-]+)";
        int lastIdx = 0;

        foreach (Match m in Regex.Matches(text, pattern))
        {
            if (m.Index > lastIdx)
                inlines.Add(new Run(text[lastIdx..m.Index]));

            if (m.Groups[1].Success) // Wiki-link
            {
                var linkText = m.Value[2..^2]; // strip [[ and ]]
                var hyperlink = new Run($"\U0001F517 {linkText}")
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 240, 255)),
                    FontWeight = FontWeights.SemiBold,
                    TextDecorations = TextDecorations.Underline
                };
                inlines.Add(hyperlink);
            }
            else if (m.Groups[2].Success) // Bold
            {
                inlines.Add(new Run(m.Value[2..^2])
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF))
                });
            }
            else if (m.Groups[3].Success) // Italic
            {
                inlines.Add(new Run(m.Value[1..^1])
                {
                    FontStyle = FontStyles.Italic,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xD0))
                });
            }
            else if (m.Groups[4].Success) // Inline code
            {
                inlines.Add(new Run(m.Value[1..^1])
                {
                    FontFamily = new FontFamily("JetBrains Mono, Consolas"),
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)),
                    Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E))
                });
            }
            else if (m.Groups[5].Success) // Strikethrough
            {
                inlines.Add(new Run(m.Value[2..^2])
                {
                    TextDecorations = TextDecorations.Strikethrough,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x8A))
                });
            }
            else if (m.Groups[6].Success) // [text](url) link
            {
                var linkMatch = Regex.Match(m.Value, @"\[([^\]]+)\]\(([^\)]+)\)");
                inlines.Add(new Run($"\U0001F517 {linkMatch.Groups[1].Value}")
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
                    TextDecorations = TextDecorations.Underline
                });
            }
            else if (m.Groups[7].Success) // Tag
            {
                inlines.Add(new Run(m.Value)
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 92, 246)),
                    FontWeight = FontWeights.SemiBold
                });
            }

            lastIdx = m.Index + m.Length;
        }

        if (lastIdx < text.Length)
            inlines.Add(new Run(text[lastIdx..]));
    }

    // ═══════════════════════════════════════
    // WIKI-LINK NAVIGATION (Ctrl+Click)
    // ═══════════════════════════════════════

    private void OnEditorMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

        var pos = _editor.GetPositionFromPoint(e.GetPosition(_editor));
        if (pos == null) return;

        var line = _editor.Document.GetLineByNumber(pos.Value.Line);
        var lineText = _editor.Document.GetText(line.Offset, line.Length);
        var col = pos.Value.Column - 1;

        // Find wiki-link at cursor position
        foreach (Match m in Regex.Matches(lineText, @"\[\[([^\]]+)\]\]"))
        {
            if (col >= m.Index && col <= m.Index + m.Length)
            {
                var linkTarget = m.Groups[1].Value;
                // Handle display name: [[file|display]]
                if (linkTarget.Contains('|'))
                    linkTarget = linkTarget.Split('|')[0];
                WikiLinkClicked?.Invoke(linkTarget);
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>Resolve a wiki-link name to a file path in the vault.</summary>
    public string? ResolveWikiLink(string linkName)
    {
        // Try exact match first
        var exact = System.IO.Path.Combine(_vaultPath, linkName + ".md");
        if (File.Exists(exact)) return exact;

        // Search recursively
        foreach (var file in Directory.EnumerateFiles(_vaultPath, "*.md", SearchOption.AllDirectories))
        {
            if (System.IO.Path.GetFileNameWithoutExtension(file)
                .Equals(linkName, StringComparison.OrdinalIgnoreCase))
                return file;
        }
        return null;
    }

    // ═══════════════════════════════════════
    // BACKLINKS
    // ═══════════════════════════════════════

    public List<(string FilePath, string Title, string Context)> GetBacklinks()
    {
        if (_currentFilePath == null) return [];
        var currentName = System.IO.Path.GetFileNameWithoutExtension(_currentFilePath);
        var results = new List<(string, string, string)>();

        foreach (var file in Directory.EnumerateFiles(_vaultPath, "*.md", SearchOption.AllDirectories))
        {
            if (file == _currentFilePath) continue;
            var content = File.ReadAllText(file);
            var pattern = $@"\[\[{Regex.Escape(currentName)}(\|[^\]]+)?\]\]";
            var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var title = System.IO.Path.GetFileNameWithoutExtension(file);
                // Get surrounding context
                int start = Math.Max(0, match.Index - 40);
                int end = Math.Min(content.Length, match.Index + match.Length + 40);
                var ctx = "..." + content[start..end].Replace("\n", " ").Trim() + "...";
                results.Add((file, title, ctx));
            }
        }
        return results;
    }

    // ═══════════════════════════════════════
    // EDITING HELPERS
    // ═══════════════════════════════════════

    public void InsertWikiLink()
    {
        var sel = _editor.SelectedText;
        if (string.IsNullOrEmpty(sel))
            _editor.Document.Insert(_editor.CaretOffset, "[[]]");
        else
            _editor.SelectedText = $"[[{sel}]]";
    }

    public void ToggleBold()
    {
        var sel = _editor.SelectedText;
        if (sel.StartsWith("**") && sel.EndsWith("**"))
            _editor.SelectedText = sel[2..^2];
        else if (!string.IsNullOrEmpty(sel))
            _editor.SelectedText = $"**{sel}**";
        else
            _editor.Document.Insert(_editor.CaretOffset, "****");
    }

    public void ToggleItalic()
    {
        var sel = _editor.SelectedText;
        if (sel.StartsWith("*") && sel.EndsWith("*") && !sel.StartsWith("**"))
            _editor.SelectedText = sel[1..^1];
        else if (!string.IsNullOrEmpty(sel))
            _editor.SelectedText = $"*{sel}*";
        else
            _editor.Document.Insert(_editor.CaretOffset, "**");
    }

    public void InsertHeading(int level)
    {
        var prefix = new string('#', level) + " ";
        var line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
        _editor.Document.Insert(line.Offset, prefix);
    }

    public void InsertCodeBlock()
    {
        _editor.SelectedText = $"```\n{_editor.SelectedText}\n```";
    }

    public void InsertLink()
    {
        var sel = _editor.SelectedText;
        _editor.SelectedText = string.IsNullOrEmpty(sel) ? "[text](url)" : $"[{sel}](url)";
    }

    public void InsertTaskList()
    {
        var line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
        _editor.Document.Insert(line.Offset, "- [ ] ");
    }
}

/// <summary>Simple ICommand for keybindings.</summary>
internal class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
