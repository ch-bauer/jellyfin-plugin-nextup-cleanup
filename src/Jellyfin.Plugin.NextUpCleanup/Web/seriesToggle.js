// Next Up Cleanup — the per-series toggle on a series detail page.
//
// Adds a button next to Play that switches this series off: while it is off, none of its
// episodes appear in Next Up or Continue Watching, whatever their episode number, play
// state or progress. The state is per user — it is stored against whoever is signed in,
// and the server reads the user from the request's own token.
(function () {
    'use strict';

    var LOG = 'Next Up Cleanup:';
    var BUTTON_CLASS = 'nuc-series-toggle';
    var CONTAINERS = ['.detailButtons', '.mainDetailButtons', '.itemActionsBottom', '.detailButtonsContainer'];

    // Series ids this user has switched off, as 32-character hex, loaded once per sign-in
    // and kept in step by the toggle itself. Null means "not loaded yet".
    var excluded = null;
    var loading = null;

    function api() {
        return window.ApiClient;
    }

    function ready() {
        var client = api();
        return client && typeof client.getUrl === 'function' && client.getCurrentUserId && client.getCurrentUserId();
    }

    function normalise(id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }

    function loadExcluded() {
        if (excluded) {
            return Promise.resolve(excluded);
        }
        if (loading) {
            return loading;
        }

        loading = api().ajax({ type: 'GET', url: api().getUrl('NextUpCleanup/Excluded'), dataType: 'json' })
            .then(function (ids) {
                excluded = {};
                (ids || []).forEach(function (id) { excluded[normalise(id)] = true; });
                loading = null;
                return excluded;
            })
            .catch(function (err) {
                console.warn(LOG, 'could not read the excluded series list', err);
                loading = null;
                excluded = {};
                return excluded;
            });

        return loading;
    }

    function isExcluded(id) {
        return !!(excluded && excluded[normalise(id)]);
    }

    function render(button, off) {
        var label = off
            ? 'Hidden from Next Up and Continue Watching'
            : 'Hide this series from Next Up and Continue Watching';

        button.classList.toggle('nuc-off', off);
        button.setAttribute('aria-pressed', off ? 'true' : 'false');
        button.setAttribute('data-nuc-state', off ? 'off' : 'on');
        button.title = label;
        button.setAttribute('aria-label', label);

        button.replaceChildren();
        var content = document.createElement('div');
        content.className = 'detailButton-content';
        var icon = document.createElement('span');
        icon.className = 'material-icons detailButton-icon';
        icon.setAttribute('aria-hidden', 'true');
        icon.textContent = off ? 'visibility_off' : 'visibility';
        content.appendChild(icon);
        button.appendChild(content);
    }

    function toast(text) {
        try {
            if (window.Dashboard && window.Dashboard.alert) {
                window.Dashboard.alert({ message: text });
            }
        } catch (e) {
            /* a missing toast is not worth breaking the toggle over */
        }
    }

    function toggle(button) {
        if (button.disabled) {
            return;
        }

        var id = button.getAttribute('data-nuc-item-id');
        var name = button.getAttribute('data-nuc-item-name') || '';
        if (!id) {
            return;
        }

        var off = !isExcluded(id);
        button.disabled = true;

        var url = api().getUrl('NextUpCleanup/Excluded/' + id + (off && name ? '?name=' + encodeURIComponent(name) : ''));

        api().ajax({ type: off ? 'POST' : 'DELETE', url: url })
            .then(function () {
                excluded = excluded || {};
                if (off) {
                    excluded[normalise(id)] = true;
                } else {
                    delete excluded[normalise(id)];
                }
                render(button, off);
                toast(off
                    ? 'Hidden from Next Up and Continue Watching.'
                    : 'Back in Next Up and Continue Watching.');
            })
            .catch(function (err) {
                console.error(LOG, 'toggle failed', err);
                toast('Could not change this series.');
            })
            .finally(function () {
                button.disabled = false;
            });
    }

    // Append, and only once. Claiming a particular slot — "immediately before the ...
    // button" — starts a fight with any other plugin that wants the same one: each shoves
    // the other aside on its own timer and both buttons flicker. Going on the end instead
    // means this never competes with Jellyfin Enhanced's buttons, however many of them are
    // switched on, and the row simply grows by one.
    function place(button, container) {
        if (button.parentNode !== container) {
            container.appendChild(button);
        }
    }

    function currentItemId() {
        var match = /[?&]id=([^&]+)/.exec(window.location.hash || '');
        return match ? match[1] : null;
    }

    function addButton(page, itemId, name) {
        var container = null;
        for (var i = 0; i < CONTAINERS.length && !container; i++) {
            container = page.querySelector(CONTAINERS[i]);
        }
        if (!container) {
            return;
        }

        var off = isExcluded(itemId);
        var button = page.querySelector('.' + BUTTON_CLASS);

        if (!button) {
            button = document.createElement('button');
            button.setAttribute('is', 'emby-button');
            button.className = 'button-flat detailButton emby-button ' + BUTTON_CLASS;
            button.type = 'button';
            button.addEventListener('click', function (event) {
                event.preventDefault();
                event.stopPropagation();
                toggle(button);
            });
        }

        place(button, container);
        var changed = button.getAttribute('data-nuc-item-id') !== itemId
            || button.getAttribute('data-nuc-state') !== (off ? 'off' : 'on');

        button.setAttribute('data-nuc-item-id', itemId);
        button.setAttribute('data-nuc-item-name', name || '');

        if (changed) {
            render(button, off);
        }
    }

    // Only series get the toggle: excluding a single movie means nothing here, and the
    // item type is not in the DOM, so it is asked for once and remembered.
    var typeCache = {};

    function isSeries(itemId) {
        var key = normalise(itemId);
        if (typeCache[key] !== undefined) {
            return Promise.resolve(typeCache[key]);
        }

        return api().getItem(api().getCurrentUserId(), itemId)
            .then(function (item) {
                typeCache[key] = item && item.Type === 'Series';
                return typeCache[key];
            })
            .catch(function () {
                return false;
            });
    }

    function tick() {
        if (!ready()) {
            return;
        }

        var page = document.querySelector('#itemDetailPage:not(.hide)');
        if (!page) {
            return;
        }

        var itemId = currentItemId();
        if (!itemId) {
            return;
        }

        loadExcluded().then(function () {
            return isSeries(itemId);
        }).then(function (series) {
            if (!series) {
                var stale = page.querySelector('.' + BUTTON_CLASS);
                if (stale && stale.parentNode) {
                    stale.parentNode.removeChild(stale);
                }
                return;
            }

            var title = page.querySelector('h1.itemName-name, h1.itemName, .itemName');
            addButton(page, itemId, title ? title.textContent.trim() : '');
        }).catch(function (err) {
            console.warn(LOG, 'could not add the series toggle', err);
        });
    }

    // The web client is a single-page app that reuses its detail page, so there is no one
    // load event to hang this on: viewshow covers navigation, and the interval covers the
    // rest of the page arriving afterwards.
    document.addEventListener('viewshow', tick);
    setInterval(tick, 700);

    console.log(LOG, 'series toggle ready');
})();
