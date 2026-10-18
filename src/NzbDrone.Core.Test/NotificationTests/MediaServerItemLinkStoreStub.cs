using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Notifications;

namespace NzbDrone.Core.Test.NotificationTests
{
    internal sealed class MediaServerItemLinkStoreStub : IMediaServerItemLinkStore
    {
        private readonly Dictionary<string, List<MediaServerItemLink>> _links = new();
        private readonly HashSet<string> _negative = new();

        public bool TryGet(string key, out List<MediaServerItemLink> links) => _links.TryGetValue(key, out links);

        public void Set(string key, IEnumerable<MediaServerItemLink> links)
        {
            _negative.Remove(key);
            _links[key] = links.ToList();
        }

        public void Remove(string key)
        {
            _links.Remove(key);
            _negative.Remove(key);
        }

        public bool IsNegative(string key) => _negative.Contains(key);

        public void SetNegative(string key)
        {
            _links.Remove(key);
            _negative.Add(key);
        }
    }
}
