# Web Translation Proxy Service

This service is used to load webpage in current webpage domain from other domains replacing URLs for current domain.
Loading in current domain is necessary for frontend to access content of the webpage and translate it. Frontend
application of webpage translation has to be loaded in the same domain as browser security forbid cross domain data
access.

URL format for making request to any webpage:
```
/{scheme}/{domain}/{**dynamicPath}
```

Example (you can start this server and open address, Debug configuration):

<https://localhost:5001/https/www.google.com/foo/bar/?foo=bar>

or (Release configuration):

<https://localhost:5001/lv/TranslateProxy/https/www.google.com/foo/bar/?foo=bar>

## Architecture
```


            ↓
            ↓   Request of `proxy-service/http/example.com`
            ↓

                ↑
                ↑   Response of `proxy-service/http/example.com` where
                ↑   resources are changed in page so that linked resources
                ↑   (for example .html pages) are linked back to proxy service
                ↑

 -------------------------------
|                               |
|         Proxy service         |
|           [Public]            |
|                               |
 -------------------------------

            ↑    ↓
            ↑    ↓  Request web page of http://example.com
            ↑    ↓

    /                       /
    /   Original web page   /
    /                       /

```