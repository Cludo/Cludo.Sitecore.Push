# Cludo.Sitecore.Push

After you installed the push plugin add the following lines to the web.config to the appSettings section:

<add key="Cludo.CustomerKey" value="YOUR CUSTOMER KEY"/>
<add key="Cludo.CustomerId" value="YOUR CUSTOMER ID"/>
<add key="Cludo.ContentId" value="YOUR CONTENT ID"/>

IMPORTANT!
1. LinkManager property languageEmbedding must be set to "never" or "always". If its set to "asNeeded" you wont be able to push to Cludo, and you will see an alert in your logs.
2. If you are using multiple host configurations, or you are publishing from different host name, then you must set targetHostName and scheme properties on site definition. Otherwise Sitecore will generate a wrong url and Cludo will not be able to crawl your website properly.