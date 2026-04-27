using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TranscribeBot.Models;

namespace TranscribeBot.Services;

internal static partial class TelegramMarkdownFormatter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough | EmphasisExtraOptions.Inserted)
        .Build();

    public static TelegramFormattedText Render(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new TelegramFormattedText(string.Empty, []);
        }

        try
        {
            var document = Markdown.Parse(text, Pipeline);
            var renderer = new Renderer();
            renderer.Render(document);
            return renderer.ToFormattedText();
        }
        catch
        {
            return new TelegramFormattedText(text, []);
        }
    }

    [GeneratedRegex("""^<\s*u\s*>$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnderlineOpenRegex();

    [GeneratedRegex("""^<\s*/\s*u\s*>$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnderlineCloseRegex();

    [GeneratedRegex("""^<\s*span\s+class\s*=\s*["']tg-spoiler["']\s*>$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpoilerOpenRegex();

    [GeneratedRegex("""^<\s*/\s*span\s*>$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpoilerCloseRegex();

    private sealed class Renderer
    {
        private readonly StringBuilder _text = new();
        private readonly List<MessageEntity> _entities = [];
        private readonly Stack<int> _underlineStarts = [];
        private readonly Stack<int> _spoilerStarts = [];

        public void Render(MarkdownDocument document)
        {
            foreach (var block in document)
            {
                AppendBlockSeparator();
                RenderBlock(block);
            }

            TrimEnd();
        }

        public TelegramFormattedText ToFormattedText()
        {
            var validEntities = _entities
                .Where(entity => entity.Length > 0 && entity.Offset >= 0 && entity.Offset + entity.Length <= _text.Length)
                .OrderBy(entity => entity.Offset)
                .ThenByDescending(entity => entity.Length)
                .ToList();

            return new TelegramFormattedText(_text.ToString(), validEntities);
        }

        private void RenderBlock(Block block)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    RenderLeafBlockWithEntity(heading, MessageEntityType.Bold);
                    break;
                case ParagraphBlock paragraph:
                    RenderLeafBlock(paragraph);
                    break;
                case QuoteBlock quote:
                    RenderContainerWithEntity(quote, MessageEntityType.Blockquote);
                    break;
                case ListBlock list:
                    RenderListBlock(list);
                    break;
                case CodeBlock codeBlock:
                    RenderCodeBlock(codeBlock);
                    break;
                case ThematicBreakBlock:
                    AppendText("---");
                    break;
                case ContainerBlock container:
                    RenderContainerBlocks(container);
                    break;
                case LeafBlock leaf:
                    RenderLeafBlock(leaf);
                    break;
            }
        }

        private void RenderContainerBlocks(ContainerBlock container)
        {
            var first = true;
            foreach (var child in container)
            {
                if (!first)
                {
                    AppendText("\n");
                }

                RenderBlock(child);
                first = false;
            }
        }

        private void RenderContainerWithEntity(ContainerBlock container, MessageEntityType entityType)
        {
            var start = _text.Length;
            RenderContainerBlocks(container);
            AddEntity(entityType, start, _text.Length - start);
        }

        private void RenderListBlock(ListBlock list)
        {
            var index = int.TryParse(list.OrderedStart, out var orderedStart) ? orderedStart : 1;
            var first = true;

            foreach (var child in list.OfType<ListItemBlock>())
            {
                if (!first)
                {
                    AppendText("\n");
                }

                AppendText(list.IsOrdered ? $"{index}. " : "- ");
                RenderListItem(child);
                index++;
                first = false;
            }
        }

        private void RenderListItem(ListItemBlock item)
        {
            var first = true;

            foreach (var child in item)
            {
                if (!first)
                {
                    AppendText("\n  ");
                }

                RenderBlock(child);
                first = false;
            }
        }

        private void RenderLeafBlockWithEntity(LeafBlock leaf, MessageEntityType entityType)
        {
            var start = _text.Length;
            RenderLeafBlock(leaf);
            AddEntity(entityType, start, _text.Length - start);
        }

        private void RenderLeafBlock(LeafBlock leaf)
        {
            if (leaf.Inline is not null)
            {
                RenderContainerInline(leaf.Inline);
                return;
            }

            AppendText(leaf.Lines.ToString());
        }

        private void RenderCodeBlock(CodeBlock codeBlock)
        {
            var start = _text.Length;
            AppendText(codeBlock.Lines.ToString().TrimEnd('\r', '\n'));

            var entity = CreateEntity(MessageEntityType.Pre, start, _text.Length - start);
            if (entity is not null && codeBlock is FencedCodeBlock fencedCodeBlock)
            {
                var language = fencedCodeBlock.Info?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(language))
                {
                    entity.Language = language;
                }
            }

            if (entity is not null)
            {
                _entities.Add(entity);
            }
        }

        private void RenderContainerInline(ContainerInline container)
        {
            var inline = container.FirstChild;
            while (inline is not null)
            {
                RenderInline(inline);
                inline = inline.NextSibling;
            }
        }

        private void RenderInline(Inline inline)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    AppendText(literal.Content.ToString());
                    break;
                case CodeInline code:
                    RenderTextWithEntity(code.Content, MessageEntityType.Code);
                    break;
                case LineBreakInline lineBreak:
                    AppendText(lineBreak.IsHard ? "\n" : " ");
                    break;
                case EmphasisInline emphasis:
                    RenderEmphasisInline(emphasis);
                    break;
                case LinkInline link:
                    RenderLinkInline(link);
                    break;
                case AutolinkInline autolink:
                    RenderAutolinkInline(autolink);
                    break;
                case HtmlInline html:
                    RenderHtmlInline(html);
                    break;
                case ContainerInline container:
                    RenderContainerInline(container);
                    break;
            }
        }

        private void RenderEmphasisInline(EmphasisInline emphasis)
        {
            var entityType = emphasis.DelimiterChar switch
            {
                '*' when emphasis.DelimiterCount >= 2 => MessageEntityType.Bold,
                '_' when emphasis.DelimiterCount >= 2 => MessageEntityType.Bold,
                '*' => MessageEntityType.Italic,
                '_' => MessageEntityType.Italic,
                '~' => MessageEntityType.Strikethrough,
                '+' => MessageEntityType.Underline,
                _ => (MessageEntityType?)null
            };

            if (entityType is null)
            {
                RenderContainerInline(emphasis);
                return;
            }

            var start = _text.Length;
            RenderContainerInline(emphasis);
            AddEntity(entityType.Value, start, _text.Length - start);
        }

        private void RenderLinkInline(LinkInline link)
        {
            if (link.IsImage)
            {
                RenderContainerInline(link);
                return;
            }

            var start = _text.Length;
            RenderContainerInline(link);
            var length = _text.Length - start;

            if (length <= 0 || string.IsNullOrWhiteSpace(link.Url))
            {
                return;
            }

            var entity = CreateEntity(MessageEntityType.TextLink, start, length);
            if (entity is null)
            {
                return;
            }

            entity.Url = link.GetDynamicUrl?.Invoke() ?? link.Url;
            _entities.Add(entity);
        }

        private void RenderAutolinkInline(AutolinkInline autolink)
        {
            RenderTextWithEntity(autolink.Url, autolink.IsEmail ? MessageEntityType.Email : MessageEntityType.Url);
        }

        private void RenderHtmlInline(HtmlInline html)
        {
            var tag = html.Tag;

            if (UnderlineOpenRegex().IsMatch(tag))
            {
                _underlineStarts.Push(_text.Length);
            }
            else if (UnderlineCloseRegex().IsMatch(tag) && _underlineStarts.TryPop(out var underlineStart))
            {
                AddEntity(MessageEntityType.Underline, underlineStart, _text.Length - underlineStart);
            }
            else if (SpoilerOpenRegex().IsMatch(tag))
            {
                _spoilerStarts.Push(_text.Length);
            }
            else if (SpoilerCloseRegex().IsMatch(tag) && _spoilerStarts.TryPop(out var spoilerStart))
            {
                AddEntity(MessageEntityType.Spoiler, spoilerStart, _text.Length - spoilerStart);
            }
        }

        private void RenderTextWithEntity(string? text, MessageEntityType entityType)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var start = _text.Length;
            AppendText(text);
            AddEntity(entityType, start, text.Length);
        }

        private void AppendBlockSeparator()
        {
            if (_text.Length > 0 && !_text.ToString().EndsWith("\n\n", StringComparison.Ordinal))
            {
                TrimEnd();
                AppendText("\n\n");
            }
        }

        private void AppendText(string? text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                _text.Append(text);
            }
        }

        private void TrimEnd()
        {
            while (_text.Length > 0 && char.IsWhiteSpace(_text[^1]))
            {
                _text.Length--;
            }
        }

        private void AddEntity(MessageEntityType type, int offset, int length)
        {
            var entity = CreateEntity(type, offset, length);
            if (entity is not null)
            {
                _entities.Add(entity);
            }
        }

        private static MessageEntity? CreateEntity(MessageEntityType type, int offset, int length)
        {
            return length <= 0
                ? null
                : new MessageEntity
                {
                    Type = type,
                    Offset = offset,
                    Length = length
                };
        }
    }
}
