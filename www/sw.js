// bedremote 的 Service Worker。
// 存在的理由只有两个：(1) Chrome 要求有它才让"装成 App"，(2) 装了 App 才会有安卓分享面板里的 bedremote。
// 分享的数据路径是 GET /share?...，服务端直接处理完再 302 回首页，所以这里**不拦截任何东西** ——
// 不缓存、不改写、不做离线：这个页面离线了也没用（电脑不在就等于不能用）。
self.addEventListener("install", function (e) { self.skipWaiting(); });
self.addEventListener("activate", function (e) { e.waitUntil(self.clients.claim()); });
