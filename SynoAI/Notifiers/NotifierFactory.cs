using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SynoAI.Notifiers.Telegram;
using System;
using System.Collections.Generic;

namespace SynoAI.Notifiers
{
    /// <summary>
    /// Handles the construction of the supported notifiers.
    /// </summary>
    public abstract class NotifierFactory
    {
        public abstract INotifier Create(ILogger logger, IConfigurationSection section);

        public static INotifier Create(NotifierType type, ILogger logger, IConfigurationSection section)
        {
            NotifierFactory factory = type switch
            {
                NotifierType.Telegram => new TelegramFactory(),
                _ => throw new NotImplementedException(type.ToString())
            };

            INotifier notifier = factory.Create(logger, section);
            notifier.Cameras = section.GetSection("Cameras").Get<List<string>>();
            notifier.Types = section.GetSection("Types").Get<List<string>>();

            return notifier;
        }
    }
}
