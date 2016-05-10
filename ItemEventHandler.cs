using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Web.Script.Serialization;
using Sitecore.Configuration;
using Sitecore.Data;
using Sitecore.Data.Items;
using Sitecore.Diagnostics;
using Sitecore.Links;
using Sitecore.Publishing;
using Sitecore.Publishing.Pipelines.PublishItem;
using Sitecore.Sites;
using Sitecore.Web;

namespace Cludo.Sitecore.Push
{
    public class ItemEventHandler
    {
        private readonly bool _isConfigurationValid;
        private readonly List<KeyValuePair<string, SiteContext>> _sites;

        public int ContentSourceId(string language)
        {
            var key = ConfigurationManager.AppSettings.Get($"Cludo.{language.ToLower()}.ContentId");
            var sourceId = 0;
            if (string.IsNullOrEmpty(key) || !int.TryParse(key, out sourceId))
            {
                key= ConfigurationManager.AppSettings.Get($"Cludo.ContentId");
                if (string.IsNullOrEmpty(key) || !int.TryParse(key, out sourceId))
                {
                    Log.Error($"ContentSourceId is not specified in appSettings or is invalid for language {language}. Please add to appSettings key Cludo.{language.ToLower()}.ContentId or default Cludo.ContentId", this);
                }
            }
            //
            return sourceId;
        }

        public int CustomerId;
        protected string CustomerKey;

        public ItemEventHandler()
        {
            _isConfigurationValid = true;
            CustomerKey = ConfigurationManager.AppSettings.Get("Cludo.CustomerKey");
            if (string.IsNullOrEmpty(CustomerKey))
            {
                Log.Error($"CludoCustomerKey is not specified in appSettings", this);
                _isConfigurationValid = false;
            }


            var key = ConfigurationManager.AppSettings.Get("Cludo.CustomerId");
            if (string.IsNullOrEmpty(key) || !int.TryParse(key, out CustomerId))
            {
                Log.Error($"CludoCustomerId is not specified in appSettings or is invalid", this);
                _isConfigurationValid = false;
            }

                        _sites = GetSites();
        }

        protected virtual Database Database => Factory.GetDatabase("web");

        protected string CludoSearchApiUrl
        {
            get
            {
                var url = ConfigurationManager.AppSettings.Get("Cludo.ServerUrl");
                return !string.IsNullOrEmpty(url) ? url : "https://api.cludo.com/";
            }
        }


        public void OnDone(object sender, EventArgs args)
        {
            if (!_isConfigurationValid) return;
            try
            {
                Process(args as ItemProcessingEventArgs);
                Process(args as ItemProcessedEventArgs);
            }
            catch (Exception ex)
            {
                Log.Error($"Search.PublishHandler:ItemProcessed; Error: {ex}", this);
            }
        }

        public void Process(ItemProcessingEventArgs args)
        {
            var context = args?.Context;
            if (context == null) return;
            if (!context.Action.Equals(PublishAction.DeleteTargetItem)) return;

            var item = Database.GetItem(context.ItemId);
            var items = new List<Item> {item};
            items.AddRange(item.Axes.GetDescendants());
            AddItemToQueue(items.ToArray());
        }

        public void Process(ItemProcessedEventArgs args)
        {
            var context = args?.Context;
            if (context == null) return;
            if (context.Result.Operation.Equals(PublishOperation.None) ||
                context.Result.Operation.Equals(PublishOperation.Skipped) ||
                context.Result.Operation.Equals(PublishOperation.Deleted)) return;

            //If the item is created or updated then test if the item should be processed
            if (context.Result.Operation.Equals(PublishOperation.Created) ||
                context.Result.Operation.Equals(PublishOperation.Updated))
            {
                //VersionToPublish is null if item version should not be published i.e. does not have this publishing target set in publishing options
                if (context.VersionToPublish == null) return;
                var contextitem = context.VersionToPublish;
                if (SkipItem(contextitem)) return;
            }
            AddItemToQueue(Database.GetItem(context.ItemId));
        }

        private List<KeyValuePair<string, SiteContext>> GetSites()
        {
            return SiteManager.GetSites()
                .Where(
                    s =>
                        !string.IsNullOrEmpty(s.Properties["rootPath"]) &&
                        !string.IsNullOrEmpty(s.Properties["startItem"]))
                .Select(
                    d => new KeyValuePair<string, SiteContext>($"{d.Properties["rootPath"]}{d.Properties["startItem"]}",
                        new SiteContext(new SiteInfo(d.Properties))))
                .ToList();
        }

        public virtual SiteContext GetSiteContext(string fullPath)
        {
            
            var site = _sites.LastOrDefault(s => fullPath.StartsWith(s.Key.ToLower()));
            return site.Value;
        }

        private void AddItemToQueue(params Item[] items)
        {
            //For now links are always added from one site as an array
            if (!items.Any()) return;
            
            var websiteItem = items.First();
            var fullItemPath = websiteItem.Paths.FullPath.ToLower();
            var site = GetSiteContext(fullItemPath);
            if (site == null)
            {
                Log.Debug($"Cludo.Push.Url can't find website for item {fullItemPath}", this);
                return;
            }

            //If there is no match for site ignore links
            using (new SiteContextSwitcher(site))
            {
                var options = LinkManager.GetDefaultUrlOptions();
                options.AlwaysIncludeServerUrl = true;
                options.ShortenUrls = true;
                options.SiteResolving = true;
                if (options.LanguageEmbedding != LanguageEmbedding.Always &&
                    options.LanguageEmbedding != LanguageEmbedding.Never)
                {
                    Log.Warn($"Cludo.Push.Url supports only linkManager with languageEmbedding as always or never https://sdn.sitecore.net/upload/sitecore6/sc62keywords/dynamic_links_sc62_a4.pdf", this);
                    return;
                }
                
                    
                foreach (var urlGroup in items.GroupBy(i => i.Language.Name))
                {
                    var contentId = ContentSourceId(urlGroup.Key);
                    if (contentId <= 0) continue;
                    using (var client = GetClient())
                    {
                        var serilizer = new JavaScriptSerializer();
                        var result = client.PostAsync($"/api/v3/{CustomerId}/content/{contentId}/pushurls",
                            new StringContent(
                                serilizer.Serialize(
                                    urlGroup.Select(item => LinkManager.GetItemUrl(item, options)).ToList()),
                                Encoding.UTF8, "application/json")).Result;

                        if (result.IsSuccessStatusCode) return;
                        var message = result.Content.ReadAsStringAsync().Result;
                        Log.Error(
                            $"Invalid request to Cludo {result.RequestMessage.RequestUri}: Status: {result.StatusCode}, Message: {message}",
                            this);
                    }
                }
            }
        }


        private HttpClient GetClient()
        {
            var client = new HttpClient
            {
                BaseAddress = new Uri(CludoSearchApiUrl)
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(CustomerId + ":" + CustomerKey)));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        protected bool SkipItem(Item item)
        {
            // skip null items and media items (media items have their own queue, populated in CustomMediaItemIndexDataSubscriber)
            return item == null || !item.Paths.IsContentItem;
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }
}