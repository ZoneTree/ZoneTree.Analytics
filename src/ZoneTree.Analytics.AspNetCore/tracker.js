;(function () {
  'use strict'
  if (window.zta && window.zta._zoneTreeInstalled) return
  var script = document.currentScript
  var attr = function (name) {
    return script ? script.getAttribute(name) : null
  }
  var endpoint =
    attr('data-endpoint') ||
    (script && script.src
      ? script.src.replace(/\/script\.js(\?.*)?$/, '/event')
      : '/_analytics/event')
  var hashRouting = attr('data-hash-routing') === 'true'
  var trackNavigation = attr('data-track-navigation') !== 'false'
  var allowLocalhost = attr('data-allow-localhost') === 'true'
  var lastUrl = null

  function currentUrl() {
    return hashRouting ? location.href : location.href.split('#')[0]
  }

  function send(name, props, metrics) {
    if (navigator.webdriver) return
    if (!allowLocalhost && /^(localhost|127\.|\[::1\])/.test(location.hostname))
      return
    var payload = { n: name, u: currentUrl(), r: document.referrer || null }
    if (hashRouting) payload.h = true
    if (props) payload.p = props
    if (metrics) payload.m = metrics
    var body = JSON.stringify(payload)
    // text/plain avoids a CORS preflight and works with sendBeacon.
    if (
      navigator.sendBeacon &&
      navigator.sendBeacon(endpoint, new Blob([body], { type: 'text/plain' }))
    )
      return
    if (window.fetch) {
      fetch(endpoint, {
        method: 'POST',
        body: body,
        keepalive: true,
        headers: { 'Content-Type': 'text/plain' },
      }).catch(function () {})
    }
  }

  function pageview() {
    var url = currentUrl()
    if (url === lastUrl) return
    lastUrl = url
    send('pageview')
  }

  if (trackNavigation && window.history) {
    ;['pushState', 'replaceState'].forEach(function (method) {
      var original = history[method]
      if (!original) return
      history[method] = function () {
        var result = original.apply(this, arguments)
        pageview()
        return result
      }
    })
    window.addEventListener('popstate', pageview)
  }
  if (trackNavigation && hashRouting)
    window.addEventListener('hashchange', pageview)

  // window.zta('signup', { plan: 'pro' }, { value: 49 }) records a custom event.
  var queued = (window.zta && window.zta.q) || []
  window.zta = function (name, props, metrics) {
    send(name, props, metrics)
  }
  window.zta._zoneTreeInstalled = true
  for (var i = 0; i < queued.length; i++) window.zta.apply(null, queued[i])

  if (document.visibilityState === 'prerender') {
    document.addEventListener('visibilitychange', function onVisible() {
      if (document.visibilityState === 'prerender') return
      document.removeEventListener('visibilitychange', onVisible)
      pageview()
    })
  } else {
    pageview()
  }
})()
