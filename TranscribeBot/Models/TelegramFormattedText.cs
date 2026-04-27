using Telegram.Bot.Types;

namespace TranscribeBot.Models;

public sealed record TelegramFormattedText(string Text, IReadOnlyCollection<MessageEntity> Entities);
