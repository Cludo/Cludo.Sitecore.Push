﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
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

        public int ContentSourceId;

        public int CustomerId;
        protected string CustomerKey;
        private SiteCollection _sites;

        public ItemEventHandler()
        {
            _isConfigurationValid = true;
            CustomerKey = ConfigurationManager.AppSettings.Get("CludoCustomerKey");
            if (string.IsNullOrEmpty(CustomerKey))
            {
                Log.Error($"CludoCustomerKey is not specified in appSettings", this);
                _isConfigurationValid = false;
            }


            var key = ConfigurationManager.AppSettings.Get("CludoCustomerId");
            if (string.IsNullOrEmpty(key) || !int.TryParse(key, out CustomerId))
            {
                Log.Error($"CludoCustomerId is not specified in appSettings or is invalid", this);
                _isConfigurationValid = false;
            }

            key = ConfigurationManager.AppSettings.Get("ContentSourceId");
            if (string.IsNullOrEmpty(key) || !int.TryParse(key, out ContentSourceId))
            {
                Log.Error($"ContentSourceId is not specified in appSettings or is invalid", this);
                _isConfigurationValid = false;
            }
            _sites = SiteManager.GetSites();
        }

        protected virtual Database Database => Factory.GetDatabase("web");

        protected string CludoSearchApiUrl
        {
            get
            {
                var url = ConfigurationManager.AppSettings.Get("CludoIndexingUrl");
                return !string.IsNullOrEmpty(url) ? url : "https://indexing.cludo.com/";
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
            var items = new List<Item> { item };
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

        private void AddItemToQueue(params Item[] items)
        {
            
            var test = SiteContext.Current;
            var options = LinkManager.GetDefaultUrlOptions();
            
            options.AlwaysIncludeServerUrl = true;
                //AddAspxExtension = true,
                //EncodeNames = true,
                //SiteResolving = true,
            options.SiteResolving = true;
            //options.Site = new SiteContext(new SiteInfo(sites[9].Properties));
            //(items[0].Paths).FullPath
            var urls = items.Select(item => LinkManager.GetItemUrl(item, options)).ToList();

            using (var client = GetClient())
            {
                var result = client.PostAsync($"/api/{CustomerId}/content/{ContentSourceId}/urlstoupdate",
                    new StringContent(JsonConvert.SerializeObject(urls), Encoding.UTF8, "application/json")).Result;

                if (result.IsSuccessStatusCode) return;
                var message = result.Content.ReadAsStringAsync().Result;
                Log.Error(message, this);
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