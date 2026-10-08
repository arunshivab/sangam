// rc.2 (Sangam.Web.Shared): the buttons of the connection box (ConnectionStatus.razor). A file of its own because the
// consoles' Content-Security-Policy allows no inline script.
(() => {
  'use strict';
  const modal = document.getElementById('components-reconnect-modal');
  if (modal) {
    // .NET 10 reports each change of state; the box shows the matching line (see sangam-connection.css).
    modal.addEventListener('components-reconnect-state-changed', (event) => {
      modal.dataset.state = event.detail && event.detail.state ? event.detail.state : '';
    });
  }

  document.addEventListener('click', async (event) => {
    const button = event.target instanceof Element ? event.target.closest('[data-conn]') : null;
    if (!button) {
      return;
    }

    if (button.dataset.conn === 'retry' && window.Blazor) {
      button.disabled = true;
      try {
        const resumed = modal && modal.dataset.state === 'paused' && typeof window.Blazor.resumeCircuit === 'function'
          ? await window.Blazor.resumeCircuit()
          : await window.Blazor.reconnect();
        if (resumed) {
          return;
        }
      } catch {
        // Fall through to a reload.
      } finally {
        button.disabled = false;
      }
    }

    location.reload();
  });
})();
