(function () {
  const endpoint = '/Home/KeepAlive';
  const intervalMs = 4 * 60 * 1000;
  let timer = null;

  async function ping() {
    if (document.hidden) return;
    try {
      await fetch(endpoint, {
        method: 'POST',
        credentials: 'include',
        headers: {
          'X-Requested-With': 'XMLHttpRequest',
          'X-App-Call': 'Angular',
          'Accept': 'application/json'
        }
      });
    } catch (_) {
      // Network hiccups must not force a logout. The next tick will retry.
    }
  }

  window.SignalTrackerSessionKeepAlive = {
    start() {
      if (timer) return;
      ping();
      timer = setInterval(ping, intervalMs);
    },
    stop() {
      if (!timer) return;
      clearInterval(timer);
      timer = null;
    },
    ping
  };

  window.SignalTrackerSessionKeepAlive.start();
})();