// Kept to ES5 without polyfills: older LG webOS, Xbox and embedded TV browsers load this file
// directly from the server and reject newer syntax outright.
(function () {
    'use strict';
    var isLoading = false;
    var maxConcurrentSections = 4;
    var selectedCategory = 'Home';
    var loadVersion = 0;

    function projectHomeSections(sections, categories, view) {
        var rows = [];
        var genreIndex = 0;
        var ordered = sections.slice().sort(function (left, right) { return left.Order - right.Order; });
        ordered.forEach(function (section) {
            var isContinueWatching = section.Id === 'Mediarow_ContinueWatching';
            var isPopular = section.Id === 'Mediarow_Popular';
            var isCatalog = section.Id.indexOf('MediarowCatalog_') === 0;
            var isAnime = (section.AdditionalData || section.DisplayTitle || '').toLowerCase() === 'anime';
            var rowCategories;
            if (categories.length === 0) {
                return;
            }
            if (view !== 'Home') {
                if (!contains(categories, view) || (isAnime && view !== 'Anime')) {
                    return;
                }
                rowCategories = [view];
            } else if (isContinueWatching || isCatalog) {
                // Admin catalog rows carry their own movie/series choice.
                rowCategories = [null];
            } else if (isPopular) {
                rowCategories = categories;
            } else {
                if (genreIndex >= 6 || (isAnime && !contains(categories, 'Anime'))) {
                    return;
                }
                rowCategories = [isAnime ? 'Anime' : categories[genreIndex % categories.length]];
                genreIndex++;
            }
            rowCategories.forEach(function (category) {
                rows.push(extend({}, section, {
                    ContentCategory: category,
                    DisplayTitle: isContinueWatching || isCatalog || !category || isAnime
                        ? section.DisplayTitle
                        : section.DisplayTitle + ' ' + category,
                    Order: rows.length
                }));
            });
        });
        return rows;
    }

    function contains(list, value) {
        return list.indexOf(value) !== -1;
    }

    function extend(target) {
        for (var index = 1; index < arguments.length; index++) {
            var source = arguments[index];
            for (var key in source) {
                if (Object.prototype.hasOwnProperty.call(source, key)) {
                    target[key] = source[key];
                }
            }
        }
        return target;
    }

    function isAttached(element) {
        if (!element) {
            return false;
        }
        return typeof element.isConnected === 'boolean'
            ? element.isConnected
            : document.documentElement.contains(element);
    }

    function findAncestor(element, className) {
        while (element && element.classList) {
            if (element.classList.contains(className)) {
                return element;
            }
            element = element.parentNode;
        }
        return null;
    }

    function getApiClient() {
        return window.ApiClient || window.apiClient;
    }

    function getUserId(apiClient) {
        return apiClient.getCurrentUserId ? apiClient.getCurrentUserId() : null;
    }

    // JSON request through Jellyfin's ApiClient (adds auth headers); plain fetch for clients without ajax().
    // A 204 / empty body resolves to null.
    function sendJson(apiClient, method, url, body) {
        var payload = JSON.stringify(body === undefined ? {} : body);
        var expectsBody = method !== 'DELETE';
        if (apiClient.ajax) {
            return apiClient.ajax({
                url: url,
                type: method,
                data: payload,
                contentType: 'application/json; charset=utf-8',
                dataType: expectsBody ? 'json' : undefined
            });
        }

        var headers = { 'Content-Type': 'application/json' };
        var accessToken = typeof apiClient.accessToken === 'function'
            ? apiClient.accessToken()
            : apiClient._accessToken || apiClient.accessToken;
        if (accessToken) {
            headers.Authorization = 'MediaBrowser Token="' + accessToken + '"';
        }

        return fetch(url, {
            method: method,
            credentials: 'same-origin',
            headers: headers,
            body: payload
        }).then(function (response) {
            if (!response.ok) {
                return response.text().then(function (text) {
                    var error = new Error(method + ' failed: ' + response.status);
                    error.status = response.status;
                    error.responseText = text;
                    throw error;
                });
            }
            return response.status === 204 || !expectsBody ? null : response.json();
        });
    }

    function resolvePlaybackItem(apiClient, itemId) {
        return sendJson(apiClient, 'POST', apiClient.getUrl('Mediarow/Playback/Resolve/' + encodeURIComponent(itemId)));
    }

    function openResolvedDetails(event, apiClient, item) {
        event.preventDefault();
        var card = event.currentTarget;
        if (card.dataset.mediarowResolving === 'true') {
            return;
        }

        card.dataset.mediarowResolving = 'true';
        card.setAttribute('aria-busy', 'true');
        var root = findAncestor(card, 'mediarowHomeRows');
        var previousError = root && root.querySelector('.mediarowPlaybackError');
        if (previousError) {
            previousError.remove();
        }

        function finish() {
            delete card.dataset.mediarowResolving;
            card.removeAttribute('aria-busy');
        }

        return Promise.resolve(resolvePlaybackItem(apiClient, item.Id)).then(function (result) {
            finish();
            var resolvedId = result.ItemId || result.itemId;
            if (resolvedId) {
                navigateToDetails(apiClient, resolvedId);
                return;
            }
            showPlaybackError(card, result.Message || result.message || 'The title is waiting for a library scan. Try again shortly.');
        }, function (error) {
            finish();
            console.warn('Mediarow playback resolve failed', error);
            var message = 'This title could not be opened for playback. Check persistence, library folders and addon settings.';
            var details = error && error.responseJSON;
            if (!details && error && error.responseText) {
                try {
                    details = JSON.parse(error.responseText);
                } catch (parseError) {
                    console.warn('Mediarow playback error response was not JSON', parseError);
                }
            }
            if (details && typeof details.Error === 'string') {
                message = details.Error;
            } else if (error && error.status === 401) {
                message = 'Your Jellyfin session has expired. Sign in again and retry.';
            } else if (error && error.status === 403) {
                message = 'Jellyfin denied this playback request. Check this user\'s permissions.';
            } else if (error && error.status === 404) {
                message = 'This catalog title or playback endpoint is unavailable. Refresh Home and check that Mediarow is installed and enabled.';
            }
            showPlaybackError(card, message);
        });
    }

    function showPlaybackError(card, message) {
        var root = findAncestor(card, 'mediarowHomeRows');
        if (!root) return;
        var alert = document.createElement('p');
        alert.className = 'mediarowPlaybackError';
        alert.setAttribute('role', 'alert');
        alert.textContent = message;
        root.insertBefore(alert, root.firstChild);
    }

    function navigateToDetails(apiClient, itemId) {
        window.location.hash = '#/details?id=' + encodeURIComponent(itemId);
    }

    function createCard(apiClient, item, index, dismissible) {
        var card = document.createElement('a');
        card.className = 'mediarowHomeCard';
        card.href = '#/details?id=' + encodeURIComponent(item.Id);
        card.title = item.Name || '';
        card.draggable = false;
        card.setAttribute('data-mediarow-index', String(index));
        card.setAttribute('data-mediarow-item', item.Id);
        card.addEventListener('click', function (event) { openResolvedDetails(event, apiClient, item); });

        var image = document.createElement('img');
        image.className = 'mediarowHomeCardImage';
        image.setAttribute('loading', 'lazy');
        image.alt = item.Name || '';
        image.draggable = false;
        image.src = apiClient.getUrl('Mediarow/Items/' + item.Id + '/Images/Primary');
        card.appendChild(image);

        if (dismissible) {
            // Kept out of the tab order so remote/arrow navigation still moves card by card; Delete on a focused card does the same.
            var dismiss = document.createElement('button');
            dismiss.type = 'button';
            dismiss.className = 'mediarowDismiss';
            dismiss.tabIndex = -1;
            dismiss.title = 'Not interested';
            dismiss.setAttribute('aria-label', 'Not interested in ' + (item.Name || 'this title'));
            dismiss.textContent = '\u00d7';
            dismiss.addEventListener('click', function (event) {
                event.preventDefault();
                event.stopPropagation();
                dismissTitle(apiClient, item);
            });
            card.appendChild(dismiss);
            card.setAttribute('aria-keyshortcuts', 'Delete');
            card.addEventListener('keydown', function (event) {
                if (event.key === 'Delete' || event.keyCode === 46) {
                    event.preventDefault();
                    dismissTitle(apiClient, item);
                }
            });
        }

        var title = document.createElement('div');
        title.className = 'mediarowHomeCardTitle';
        title.textContent = item.Name || '';
        card.appendChild(title);
        return card;
    }

    function preferencesUrl(apiClient, path) {
        return apiClient.getUrl('Plugins/AIOStreams/' + path + '?userId=' + encodeURIComponent(getUserId(apiClient)));
    }

    // Removes a title from every rendered row and See all grid at once. Rows are rebuilt from their
    // remaining items so the carousel clones and keyboard order stay consistent.
    function removeTitleEverywhere(apiClient, itemId) {
        Array.prototype.slice.call(document.querySelectorAll('.mediarowHomeRow')).forEach(function (row) {
            if (!row.mediarowSection || !row.querySelector('[data-mediarow-item="' + itemId + '"]')) {
                return;
            }
            var remaining = (row.mediarowItems || []).filter(function (item) { return item.Id !== itemId; });
            var replacement = createRow(apiClient, row.mediarowSection, { Items: remaining });
            if (replacement) {
                row.parentNode.replaceChild(replacement, row);
            } else {
                row.parentNode.removeChild(row);
            }
        });
        Array.prototype.slice.call(document.querySelectorAll('.mediarowSeeAllCard[data-mediarow-item="' + itemId + '"]')).forEach(function (card) {
            card.parentNode.removeChild(card);
        });
    }

    function dismissTitle(apiClient, item) {
        var name = item.Name || 'This title';
        var url = preferencesUrl(apiClient, 'NotInterested/' + encodeURIComponent(item.Id));
        removeTitleEverywhere(apiClient, item.Id);
        var saved = Promise.resolve(sendJson(apiClient, 'POST', url)).then(function () { return true; }, function (error) {
            console.warn('Mediarow could not save Not interested', error);
            showToast('Could not hide ' + name + '. Try again.');
            refreshHome();
            return false;
        });
        showToast(name + ' won\u2019t be suggested again.', 'Undo', function () {
            // Undo waits for the save so the DELETE can never overtake the POST.
            saved.then(function (wasSaved) {
                return wasSaved ? sendJson(apiClient, 'DELETE', url) : null;
            }).then(refreshHome, function (error) {
                console.warn('Mediarow could not undo Not interested', error);
                showToast('Could not undo. Remove it under Customize instead.');
            });
        });
    }

    var toastTimer = null;

    function showToast(message, actionLabel, action) {
        var existing = document.querySelector('.mediarowToast');
        if (existing) {
            existing.parentNode.removeChild(existing);
        }
        clearTimeout(toastTimer);
        var toast = document.createElement('div');
        toast.className = 'mediarowToast';
        toast.setAttribute('role', 'status');
        var text = document.createElement('span');
        text.textContent = message;
        toast.appendChild(text);
        function hide() {
            if (toast.parentNode) {
                toast.parentNode.removeChild(toast);
            }
        }
        if (actionLabel && action) {
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'mediarowToastAction';
            button.textContent = actionLabel;
            button.addEventListener('click', function () {
                hide();
                action();
            });
            toast.appendChild(button);
        }
        document.body.appendChild(toast);
        toastTimer = setTimeout(hide, 7000);
    }

    function refreshHome() {
        var root = document.querySelector('[data-mediarow-home="true"]');
        if (root && root.parentNode) {
            root.parentNode.removeChild(root);
        }
        isLoading = false;
        return loadRows();
    }

    function setupCarousel(apiClient, items, catalogItems, previousButton, nextButton, dismissible) {
        var originals = Array.prototype.slice.call(items.children);
        var cycleWidth = 0;
        var pointer = null;
        var suppressClick = false;
        var lastWidth = 0;

        function stillAttached() {
            return typeof items.isConnected === 'boolean'
                ? items.isConnected
                : document.documentElement.contains(items);
        }

        function normalizePosition(position) {
            if (!cycleWidth) {
                return Math.max(0, Math.min(position, items.scrollWidth - items.clientWidth));
            }
            return cycleWidth + ((position - cycleWidth) % cycleWidth + cycleWidth) % cycleWidth;
        }

        function moveBy(distance) {
            items.scrollLeft = normalizePosition(items.scrollLeft + distance);
        }

        function updateLayout() {
            var width = items.clientWidth;
            if (!width || width === lastWidth) {
                return;
            }
            lastWidth = width;
            var oldPosition = cycleWidth ? items.scrollLeft - cycleWidth : items.scrollLeft;
            Array.prototype.slice.call(items.querySelectorAll('[data-carousel-copy]')).forEach(function (card) {
                card.remove();
            });
            cycleWidth = 0;
            var overflowing = items.scrollWidth > width + 1;
            previousButton.hidden = nextButton.hidden = !overflowing;
            if (overflowing) {
                var prefix = document.createDocumentFragment();
                var suffix = document.createDocumentFragment();
                catalogItems.forEach(function (item, index) {
                    [prefix, suffix].forEach(function (fragment) {
                        var card = createCard(apiClient, item, index, dismissible);
                        card.dataset.carouselCopy = 'true';
                        card.setAttribute('aria-hidden', 'true');
                        card.tabIndex = -1;
                        fragment.appendChild(card);
                    });
                });
                items.insertBefore(prefix, originals[0]);
                items.appendChild(suffix);
                cycleWidth = originals[0].offsetLeft - items.children[0].offsetLeft;
                items.scrollLeft = normalizePosition(cycleWidth + oldPosition);
            } else {
                items.scrollLeft = 0;
            }
        }

        previousButton.addEventListener('click', function () { moveBy(-items.clientWidth * .8); });
        nextButton.addEventListener('click', function () { moveBy(items.clientWidth * .8); });
        items.addEventListener('scroll', function () {
            if (cycleWidth && (items.scrollLeft < cycleWidth || items.scrollLeft >= cycleWidth * 2)) {
                items.scrollLeft = normalizePosition(items.scrollLeft);
            }
        }, { passive: true });
        items.addEventListener('pointerdown', function (event) {
            // Touch keeps the browser's native momentum scrolling; only mouse/pen drags are emulated.
            if (!event.isPrimary || event.button !== 0 || event.pointerType === 'touch') {
                return;
            }
            suppressClick = false;
            pointer = { id: event.pointerId, startX: event.clientX, startY: event.clientY, lastX: event.clientX, dragging: false };
        });
        items.addEventListener('pointermove', function (event) {
            if (!pointer || pointer.id !== event.pointerId) {
                return;
            }
            if (!pointer.dragging) {
                var distanceX = Math.abs(event.clientX - pointer.startX);
                var distanceY = Math.abs(event.clientY - pointer.startY);
                if (distanceX < 8 && distanceY < 8) {
                    return;
                }
                if (distanceY > distanceX) {
                    pointer = null;
                    return;
                }
                pointer.dragging = true;
                suppressClick = true;
                items.setPointerCapture(event.pointerId);
                items.classList.add('isDragging');
            }
            event.preventDefault();
            moveBy(pointer.lastX - event.clientX);
            pointer.lastX = event.clientX;
        });
        function finishDrag(event) {
            if (!pointer || pointer.id !== event.pointerId) {
                return;
            }
            pointer = null;
            items.classList.remove('isDragging');
            if (items.hasPointerCapture(event.pointerId)) {
                items.releasePointerCapture(event.pointerId);
            }
        }
        items.addEventListener('pointerup', finishDrag);
        items.addEventListener('pointercancel', finishDrag);
        items.addEventListener('lostpointercapture', finishDrag);
        items.addEventListener('pointerleave', function (event) {
            if (pointer && !pointer.dragging) {
                finishDrag(event);
            }
        });
        items.addEventListener('click', function (event) {
            if (suppressClick && event.detail !== 0) {
                event.preventDefault();
                event.stopImmediatePropagation();
            }
        }, true);
        items.addEventListener('dragstart', function (event) { event.preventDefault(); });
        // Remote controls, gamepads and keyboards move focus card by card and wrap at the ends.
        // Handling the key here also stops Jellyfin's spatial navigation from leaving the row sideways.
        items.addEventListener('keydown', function (event) {
            var key = event.key || (event.keyCode === 37 ? 'ArrowLeft' : event.keyCode === 39 ? 'ArrowRight' : '');
            if (key !== 'ArrowLeft' && key !== 'ArrowRight') {
                return;
            }
            var direction = key === 'ArrowLeft' ? -1 : 1;
            event.preventDefault();
            var index = event.target && event.target.getAttribute
                ? parseInt(event.target.getAttribute('data-mediarow-index'), 10)
                : NaN;
            if (isNaN(index) || !originals.length) {
                moveBy(direction * items.clientWidth * .8);
                return;
            }
            originals[(index + direction + originals.length) % originals.length].focus();
        });
        if (window.ResizeObserver) {
            var resizeObserver = new ResizeObserver(function () {
                if (!stillAttached()) {
                    resizeObserver.disconnect();
                    return;
                }
                updateLayout();
            });
            resizeObserver.observe(items);
        } else {
            window.addEventListener('resize', function onResize() {
                if (!stillAttached()) {
                    window.removeEventListener('resize', onResize);
                    return;
                }
                updateLayout();
            });
        }
        requestAnimationFrame(updateLayout);
    }

    var seeAllPageSize = 40;

    function catalogPageUrl(apiClient, section, pageIndex) {
        var query = '?userId=' + encodeURIComponent(getUserId(apiClient)) +
            '&pageIndex=' + pageIndex + '&pageSize=' + seeAllPageSize;
        if (section.ContentCategory) {
            query += '&contentCategory=' + encodeURIComponent(section.ContentCategory);
        }
        return apiClient.getUrl('Mediarow/Home/Catalog/' + encodeURIComponent(section.Id) + '/Page' + query);
    }

    // Full-screen grid for one row. Pages are requested with pageIndex/pageSize; the server turns them
    // into upstream offsets (Stremio skip / TMDB page) so page 2+ continues where page 1 ended.
    function openSeeAll(apiClient, section) {
        var existing = document.querySelector('.mediarowSeeAllPanel');
        if (existing) {
            existing.parentNode.removeChild(existing);
        }
        var opener = document.activeElement;
        var panel = document.createElement('div');
        panel.className = 'mediarowSeeAllPanel';
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-modal', 'true');
        panel.setAttribute('aria-label', section.DisplayTitle);

        var header = document.createElement('div');
        header.className = 'mediarowSeeAllHeader';
        var title = document.createElement('h2');
        title.className = 'mediarowSeeAllTitle';
        title.textContent = section.DisplayTitle;
        var close = document.createElement('button');
        close.type = 'button';
        close.className = 'mediarowSeeAllClose';
        close.setAttribute('aria-label', 'Close ' + section.DisplayTitle);
        close.textContent = '\u00d7';
        header.appendChild(title);
        header.appendChild(close);
        panel.appendChild(header);

        var grid = document.createElement('div');
        grid.className = 'mediarowSeeAllGrid';
        panel.appendChild(grid);
        var status = document.createElement('p');
        status.className = 'mediarowSeeAllStatus';
        status.setAttribute('role', 'status');
        panel.appendChild(status);
        var more = document.createElement('button');
        more.type = 'button';
        more.className = 'mediarowSeeAllMore';
        more.textContent = 'Load more';
        more.hidden = true;
        panel.appendChild(more);

        var nextPage = 0;
        var loading = false;
        var seen = {};
        var count = 0;

        function dismiss() {
            if (!panel.parentNode) {
                return;
            }
            panel.parentNode.removeChild(panel);
            document.removeEventListener('keydown', onKey, true);
            window.removeEventListener('hashchange', dismiss);
            document.documentElement.classList.remove('mediarowSeeAllOpen');
            if (opener && opener.focus && isAttached(opener)) {
                opener.focus();
            }
        }

        function onKey(event) {
            if (event.key === 'Escape' || event.key === 'Backspace' || event.keyCode === 27 || event.keyCode === 461 || event.keyCode === 10009) {
                if (event.key === 'Backspace' && /^(INPUT|TEXTAREA)$/.test((event.target || {}).tagName || '')) {
                    return;
                }
                event.preventDefault();
                event.stopPropagation();
                dismiss();
            }
        }

        function loadPage() {
            if (loading) {
                return;
            }
            loading = true;
            more.disabled = true;
            status.textContent = nextPage === 0 ? 'Loading...' : 'Loading more...';
            var pageIndex = nextPage;
            Promise.resolve(apiClient.getJSON(catalogPageUrl(apiClient, section, pageIndex))).then(function (page) {
                if (!panel.parentNode) {
                    return;
                }
                var firstNew = null;
                (page.Items || []).forEach(function (item) {
                    if (seen[item.Id]) {
                        return;
                    }
                    seen[item.Id] = true;
                    var card = createCard(apiClient, item, count++, true);
                    card.className += ' mediarowSeeAllCard';
                    grid.appendChild(card);
                    firstNew = firstNew || card;
                });
                nextPage = pageIndex + 1;
                more.hidden = !page.HasMore;
                status.textContent = count === 0 ? 'Nothing to show in this row.' : (page.HasMore ? '' : count + ' titles');
                if (pageIndex > 0 && firstNew) {
                    firstNew.focus();
                } else if (pageIndex === 0) {
                    (grid.firstChild || close).focus();
                }
            }, function (error) {
                console.warn('Mediarow See all failed to load', error);
                status.textContent = 'Could not load more titles. Try again.';
                more.hidden = false;
            }).then(function () {
                loading = false;
                more.disabled = false;
            });
        }

        close.addEventListener('click', dismiss);
        more.addEventListener('click', loadPage);
        document.addEventListener('keydown', onKey, true);
        // Opening a title changes the hash; the panel must not cover the details page.
        window.addEventListener('hashchange', dismiss);
        document.documentElement.classList.add('mediarowSeeAllOpen');
        document.body.appendChild(panel);
        close.focus();
        loadPage();
    }

    function moveInList(list, index, delta) {
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.length) {
            return list.slice();
        }
        var copy = list.slice();
        var moved = copy.splice(index, 1)[0];
        copy.splice(target, 0, moved);
        return copy;
    }

    var sectionKindLabels = {
        ContinueWatching: 'Your progress',
        Popular: 'Trending now',
        Catalog: 'Catalog',
        Genre: 'Genre'
    };

    function el(tag, className, text) {
        var element = document.createElement(tag);
        if (className) {
            element.className = className;
        }
        if (text !== undefined) {
            element.textContent = text;
        }
        return element;
    }

    function createSwitch(label, checked, onChange) {
        var input = el('input', 'mediarowSwitch');
        input.type = 'checkbox';
        input.checked = checked;
        input.setAttribute('role', 'switch');
        input.setAttribute('aria-label', label);
        input.addEventListener('change', function () { onChange(input.checked); });
        return input;
    }

    // Personal Home controls: row order and visibility, Hide watched and the Not interested list.
    // Layout changes are saved together; Not interested removals apply immediately.
    function openCustomize(apiClient) {
        var existing = document.querySelector('.mediarowCustomize');
        if (existing) {
            existing.parentNode.removeChild(existing);
        }
        var opener = document.activeElement;
        var state = null;
        var homeChanged = false;

        var panel = el('div', 'mediarowCustomize');
        var backdrop = el('div', 'mediarowCustomizeBackdrop');
        var sheet = el('div', 'mediarowCustomizeSheet');
        sheet.setAttribute('role', 'dialog');
        sheet.setAttribute('aria-modal', 'true');
        sheet.setAttribute('aria-labelledby', 'mediarowCustomizeTitle');
        panel.appendChild(backdrop);
        panel.appendChild(sheet);

        var header = el('header', 'mediarowCustomizeHeader');
        var headings = el('div');
        headings.appendChild(el('p', 'mediarowCustomizeEyebrow', 'Personal Home'));
        var title = el('h2', 'mediarowCustomizeTitle', 'Make Home yours');
        title.id = 'mediarowCustomizeTitle';
        headings.appendChild(title);
        var close = el('button', 'mediarowSeeAllClose', '\u00d7');
        close.type = 'button';
        close.setAttribute('aria-label', 'Close Home settings');
        header.appendChild(headings);
        header.appendChild(close);
        sheet.appendChild(header);

        var body = el('div', 'mediarowCustomizeBody');
        sheet.appendChild(body);
        var status = el('p', 'mediarowCustomizeStatus', 'Loading your Home\u2026');
        status.setAttribute('role', 'status');
        body.appendChild(status);

        var footer = el('footer', 'mediarowCustomizeFooter');
        var reset = el('button', 'mediarowCustomizeGhost', 'Reset rows');
        reset.type = 'button';
        var save = el('button', 'mediarowCustomizePrimary', 'Save');
        save.type = 'button';
        footer.appendChild(reset);
        footer.appendChild(save);
        sheet.appendChild(footer);
        reset.disabled = save.disabled = true;

        function dismiss() {
            if (!panel.parentNode) {
                return;
            }
            panel.parentNode.removeChild(panel);
            document.removeEventListener('keydown', onKey, true);
            window.removeEventListener('hashchange', dismiss);
            document.documentElement.classList.remove('mediarowSeeAllOpen');
            if (homeChanged) {
                refreshHome();
            } else if (opener && opener.focus && isAttached(opener)) {
                opener.focus();
            }
        }

        function onKey(event) {
            if (event.key === 'Escape' || event.key === 'Backspace' || event.keyCode === 27 || event.keyCode === 461 || event.keyCode === 10009) {
                if (event.key === 'Backspace' && /^(INPUT|TEXTAREA)$/.test((event.target || {}).tagName || '') && event.target.type !== 'checkbox') {
                    return;
                }
                event.preventDefault();
                event.stopPropagation();
                dismiss();
            }
        }

        function load(focusSelector) {
            status.textContent = 'Loading your Home\u2026';
            status.hidden = false;
            return Promise.resolve(apiClient.getJSON(preferencesUrl(apiClient, 'UserPreferences'))).then(function (preferences) {
                state = {
                    hideWatched: !!preferences.HideWatched,
                    sections: (preferences.Sections || []).map(function (section) {
                        return { id: section.Id, title: section.DisplayTitle, kind: section.Kind, visible: section.Visible };
                    }),
                    notInterested: preferences.NotInterested || []
                };
                reset.disabled = save.disabled = false;
                render(focusSelector);
            }, function (error) {
                console.warn('Mediarow could not load Home preferences', error);
                status.textContent = 'Could not load your Home settings. Close and try again.';
            });
        }

        function render(focusSelector) {
            while (body.firstChild) {
                body.removeChild(body.firstChild);
            }

            var watched = el('section', 'mediarowCustomizeGroup');
            var watchedRow = el('label', 'mediarowCustomizeToggle');
            var watchedText = el('span', 'mediarowCustomizeToggleText');
            watchedText.appendChild(el('strong', null, 'Hide watched titles'));
            watchedText.appendChild(el('span', 'mediarowCustomizeHint', 'Movies and shows you have finished disappear from every row.'));
            watchedRow.appendChild(watchedText);
            watchedRow.appendChild(createSwitch('Hide watched titles', state.hideWatched, function (checked) { state.hideWatched = checked; }));
            watched.appendChild(watchedRow);
            body.appendChild(watched);

            var rows = el('section', 'mediarowCustomizeGroup');
            rows.appendChild(el('h3', 'mediarowCustomizeGroupTitle', 'Rows'));
            rows.appendChild(el('p', 'mediarowCustomizeHint', 'Move rows with the arrows and switch off the ones you never use. Home shows the first six genre rows.'));
            var list = el('ol', 'mediarowCustomizeRows');
            state.sections.forEach(function (section, index) {
                var item = el('li', 'mediarowCustomizeRow' + (section.visible ? '' : ' isHidden'));
                item.setAttribute('data-section', section.id);
                item.appendChild(el('span', 'mediarowCustomizeIndex', (index < 9 ? '0' : '') + (index + 1)));
                var text = el('span', 'mediarowCustomizeRowText');
                text.appendChild(el('span', 'mediarowCustomizeRowTitle', section.title));
                text.appendChild(el('span', 'mediarowCustomizeKind', sectionKindLabels[section.kind] || section.kind));
                item.appendChild(text);
                [['up', '\u2191', -1, 'Move ' + section.title + ' up'], ['down', '\u2193', 1, 'Move ' + section.title + ' down']].forEach(function (spec) {
                    var button = el('button', 'mediarowCustomizeMove', spec[1]);
                    button.type = 'button';
                    button.setAttribute('data-move', spec[0]);
                    button.setAttribute('aria-label', spec[3]);
                    button.disabled = index + spec[2] < 0 || index + spec[2] >= state.sections.length;
                    button.addEventListener('click', function () {
                        state.sections = moveInList(state.sections, index, spec[2]);
                        var targetIndex = index + spec[2];
                        var edge = targetIndex === 0 || targetIndex === state.sections.length - 1;
                        // Keep focus on the moved row; at an edge the pressed arrow is disabled, so use the other one.
                        render('[data-section="' + section.id + '"] [data-move="' + (edge ? (spec[0] === 'up' ? 'down' : 'up') : spec[0]) + '"]');
                    });
                    item.appendChild(button);
                });
                item.appendChild(createSwitch('Show ' + section.title, section.visible, function (checked) {
                    section.visible = checked;
                    item.className = 'mediarowCustomizeRow' + (checked ? '' : ' isHidden');
                }));
                list.appendChild(item);
            });
            rows.appendChild(list);
            body.appendChild(rows);

            var dismissed = el('section', 'mediarowCustomizeGroup');
            dismissed.appendChild(el('h3', 'mediarowCustomizeGroupTitle', 'Not interested' + (state.notInterested.length ? ' \u00b7 ' + state.notInterested.length : '')));
            if (!state.notInterested.length) {
                dismissed.appendChild(el('p', 'mediarowCustomizeHint', 'Titles you dismiss with \u00d7 on a poster are listed here so you can bring them back.'));
            }
            var hiddenList = el('ul', 'mediarowCustomizeDismissed');
            state.notInterested.forEach(function (entry) {
                var item = el('li', 'mediarowCustomizeDismissedItem');
                item.appendChild(el('span', null, (entry.Name || 'Unknown title') + (entry.ProductionYear ? ' (' + entry.ProductionYear + ')' : '')));
                var restore = el('button', 'mediarowCustomizeGhost', 'Show again');
                restore.type = 'button';
                restore.setAttribute('aria-label', 'Show ' + (entry.Name || 'this title') + ' again');
                restore.addEventListener('click', function () {
                    restore.disabled = true;
                    Promise.resolve(sendJson(apiClient, 'DELETE', preferencesUrl(apiClient, 'NotInterested/' + encodeURIComponent(entry.ItemId)))).then(function () {
                        homeChanged = true;
                        state.notInterested = state.notInterested.filter(function (other) { return other.ItemId !== entry.ItemId; });
                        render('.mediarowCustomizeDismissed button, .mediarowCustomizePrimary');
                    }, function (error) {
                        console.warn('Mediarow could not restore title', error);
                        restore.disabled = false;
                        showToast('Could not restore ' + (entry.Name || 'this title') + '.');
                    });
                });
                item.appendChild(restore);
                hiddenList.appendChild(item);
            });
            dismissed.appendChild(hiddenList);
            body.appendChild(dismissed);

            var focusTarget = focusSelector && sheet.querySelector(focusSelector);
            (focusTarget || close).focus();
        }

        save.addEventListener('click', function () {
            if (!state) {
                return;
            }
            save.disabled = true;
            save.textContent = 'Saving\u2026';
            Promise.resolve(sendJson(apiClient, 'POST', preferencesUrl(apiClient, 'UserPreferences'), {
                UserId: getUserId(apiClient),
                HideWatched: state.hideWatched,
                SectionOrder: state.sections.map(function (section) { return section.id; }),
                HiddenSections: state.sections.filter(function (section) { return !section.visible; }).map(function (section) { return section.id; })
            })).then(function () {
                homeChanged = true;
                dismiss();
            }, function (error) {
                console.warn('Mediarow could not save Home preferences', error);
                save.disabled = false;
                save.textContent = 'Save';
                showToast('Could not save your Home settings. Try again.');
            });
        });

        reset.addEventListener('click', function () {
            reset.disabled = true;
            Promise.resolve(sendJson(apiClient, 'POST', preferencesUrl(apiClient, 'UserPreferences'), {
                UserId: getUserId(apiClient),
                SectionOrder: [],
                HiddenSections: []
            })).then(function () {
                homeChanged = true;
                return load('.mediarowCustomizeFooter .mediarowCustomizeGhost');
            }, function (error) {
                console.warn('Mediarow could not reset Home rows', error);
                reset.disabled = false;
                showToast('Could not reset rows. Try again.');
            });
        });

        close.addEventListener('click', dismiss);
        backdrop.addEventListener('click', dismiss);
        document.addEventListener('keydown', onKey, true);
        window.addEventListener('hashchange', dismiss);
        document.documentElement.classList.add('mediarowSeeAllOpen');
        document.body.appendChild(panel);
        close.focus();
        load();
    }

    function createRow(apiClient, section, result) {
        var row = document.createElement('section');
        row.className = 'mediarowHomeRow';
        row.setAttribute('data-mediarow-section', section.Id);
        row.setAttribute('data-mediarow-order', String(section.Order));
        row.setAttribute('data-mediarow-category', section.ContentCategory || 'Mixed');

        var heading = document.createElement('h2');
        heading.className = 'mediarowHomeRowTitle';
        heading.textContent = section.DisplayTitle;
        var header = document.createElement('div');
        header.className = 'mediarowHomeRowHeader';
        header.appendChild(heading);
        var controls = document.createElement('div');
        controls.className = 'mediarowCarouselControls';
        function createButton(label, text) {
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'mediarowCarouselButton';
            button.setAttribute('aria-label', label + ' in ' + section.DisplayTitle);
            button.textContent = text;
            button.hidden = true;
            controls.appendChild(button);
            return button;
        }
        var previousButton = createButton('Previous items', '<');
        var nextButton = createButton('Next items', '>');
        if (section.Id !== 'Mediarow_ContinueWatching') {
            var seeAll = document.createElement('button');
            seeAll.type = 'button';
            seeAll.className = 'mediarowSeeAll';
            seeAll.textContent = 'See all';
            seeAll.setAttribute('aria-label', 'See all ' + section.DisplayTitle);
            seeAll.addEventListener('click', function () { openSeeAll(apiClient, section); });
            header.appendChild(seeAll);
        }
        header.appendChild(controls);
        row.appendChild(header);

        var items = document.createElement('div');
        items.className = 'mediarowHomeRowItems';
        items.setAttribute('role', 'group');
        items.setAttribute('aria-label', section.DisplayTitle + ' carousel. Drag, swipe or use the arrow keys to browse.');
        var dismissible = section.Id !== 'Mediarow_ContinueWatching';
        (result.Items || []).forEach(function (item, index) {
            items.appendChild(createCard(apiClient, item, index, dismissible));
        });
        if (items.children.length > 0) {
            row.appendChild(items);
            row.mediarowSection = section;
            row.mediarowItems = result.Items;
            setupCarousel(apiClient, items, result.Items, previousButton, nextButton, dismissible);
            return row;
        }
        return null;
    }

    function insertRowInOrder(root, row) {
        var order = Number(row.getAttribute('data-mediarow-order'));
        var nextRow = null;
        for (var index = 0; index < root.children.length; index++) {
            if (Number(root.children[index].getAttribute('data-mediarow-order')) > order) {
                nextRow = root.children[index];
                break;
            }
        }
        root.insertBefore(row, nextRow);
    }

    // Jellyfin's "Recently Added" block is the only built-in Home section rendered without
    // .verticalSection. Every title opened through Mediarow lands there, so keep it below our rows.
    function moveRecentlyAddedBelow(root) {
        var container = root && root.parentElement;
        if (!container) {
            return;
        }
        Array.prototype.slice.call(container.children).forEach(function (child) {
            if (child !== root && /(?:^|\s)section\d+(?:\s|$)/.test(child.className) &&
                !child.classList.contains('verticalSection') &&
                (root.compareDocumentPosition(child) & Node.DOCUMENT_POSITION_PRECEDING)) {
                container.appendChild(child);
            }
        });
    }

    function loadRows() {
        var apiClient = getApiClient();
        if (!/^#\/home(?:[?]|$)/.test(window.location.hash) ||
            !apiClient || !apiClient.getJSON || isLoading || document.querySelector('[data-mediarow-home="true"]')) {
            return Promise.resolve();
        }

        var userId = getUserId(apiClient);
        if (!userId) {
            return Promise.resolve();
        }

        isLoading = true;
        var currentLoadVersion = ++loadVersion;
        var root = null;

        function finish() {
            if (currentLoadVersion === loadVersion) {
                isLoading = false;
                if (root && !isAttached(root)) {
                    setTimeout(loadRows, 0);
                }
            }
        }

        function showFailure(error) {
            console.warn('Mediarow Home rows failed to load', error);
            if (currentLoadVersion === loadVersion && /^#\/home(?:[?]|$)/.test(window.location.hash)) {
                var errorContainer = findHomeContainer();
                if (!root && errorContainer) {
                    root = document.createElement('div');
                    root.dataset.mediarowHome = 'true';
                    root.className = 'mediarowHomeRows';
                    errorContainer.appendChild(root);
                }
                if (root) {
                    var message = document.createElement('p');
                    message.setAttribute('role', 'alert');
                    message.textContent = 'Mediarow could not load. Refresh to retry and check the server catalog settings.';
                    root.appendChild(message);
                }
            }
        }

        return Promise.resolve(apiClient.getJSON(apiClient.getUrl('Mediarow/Home/Sections?userId=' + encodeURIComponent(userId))))
            .then(function (sectionsResponse) {
                var categories = (sectionsResponse.ContentCategories || []).filter(function (category) {
                    return contains(['Movies', 'Series', 'Anime'], category);
                });
                if (selectedCategory !== 'Home' && !contains(categories, selectedCategory)) {
                    selectedCategory = 'Home';
                }
                var sections = projectHomeSections(sectionsResponse.Items || [], categories, selectedCategory);
                var container = findHomeContainer();
                if (!container || currentLoadVersion !== loadVersion || !/^#\/home(?:[?]|$)/.test(window.location.hash)) {
                    return null;
                }

                root = document.createElement('div');
                root.dataset.mediarowHome = 'true';
                root.className = 'mediarowHomeRows';
                container.appendChild(root);
                moveRecentlyAddedBelow(root);

                var filters = document.createElement('nav');
                filters.className = 'mediarowHomeFilters';
                filters.setAttribute('aria-label', 'Browse Mediarow content');
                ['Home'].concat(categories).forEach(function (category) {
                    var button = document.createElement('button');
                    button.type = 'button';
                    button.className = 'mediarowHomeFilter';
                    button.textContent = category;
                    button.setAttribute('aria-pressed', String(category === selectedCategory));
                    button.addEventListener('click', function () {
                        if (category !== selectedCategory) {
                            selectedCategory = category;
                            root.remove();
                            isLoading = false;
                            loadRows();
                        }
                    });
                    filters.appendChild(button);
                });
                var customize = document.createElement('button');
                customize.type = 'button';
                customize.className = 'mediarowHomeFilter mediarowCustomizeButton';
                customize.textContent = 'Customize';
                customize.setAttribute('aria-haspopup', 'dialog');
                customize.addEventListener('click', function () { openCustomize(apiClient); });
                filters.appendChild(customize);
                root.appendChild(filters);
                var status = document.createElement('p');
                status.className = 'mediarowHomeStatus';
                status.setAttribute('role', 'status');
                status.textContent = sections.length ? 'Loading ' + selectedCategory.toLowerCase() + ' rows...' : 'No rows to show. Turn rows back on under Customize, or enable content categories in Mediarow settings.';
                root.appendChild(status);
                var rowsContainer = document.createElement('div');
                root.appendChild(rowsContainer);
                var nextSectionIndex = 0;
                var failedSections = 0;

                function loadNextSection() {
                    if (!isAttached(root) || nextSectionIndex >= sections.length) {
                        return Promise.resolve();
                    }
                    var section = sections[nextSectionIndex++];
                    var query = '?userId=' + encodeURIComponent(userId);
                    if (section.ContentCategory) {
                        query += '&contentCategory=' + encodeURIComponent(section.ContentCategory);
                    }
                    return Promise.resolve(apiClient.getJSON(apiClient.getUrl('Mediarow/Home/Section/' + encodeURIComponent(section.Id) + query)))
                        .then(function (result) {
                            var row = createRow(apiClient, section, result);
                            if (row && isAttached(root)) {
                                insertRowInOrder(rowsContainer, row);
                            }
                        }, function (error) {
                            failedSections++;
                            console.warn('Mediarow Home section failed to load', section.Id, error);
                        })
                        .then(loadNextSection);
                }

                var workers = [];
                for (var workerIndex = 0; workerIndex < Math.min(maxConcurrentSections, sections.length); workerIndex++) {
                    workers.push(loadNextSection());
                }
                return Promise.all(workers).then(function () {
                    if (sections.length > 0) {
                        status.hidden = rowsContainer.children.length > 0 && failedSections === 0;
                        status.textContent = failedSections > 0
                            ? 'Some rows could not load. Select another category or refresh to retry.'
                            : 'No results for this category from your catalog provider.';
                    }
                });
            })
            .then(null, showFailure)
            .then(finish);
    }

    function findHomeContainer() {
        return document.querySelector('.sections') || document.querySelector('.homeSections') || document.querySelector('.homePage');
    }

    var NEXT_EPISODE_COUNTDOWN_SECONDS = 10;
    // Jellyfin's own prompt opens at most 40 seconds before the end; earlier means the credits started.
    var NATIVE_UP_NEXT_SECONDS = 45;

    // Jellyfin counts down to the end of the file, which can include minutes of credits.
    // When the prompt opened at the credits, Netflix-style: count down a few seconds and start the next episode.
    function nextEpisodeCountdown(state, nativeRemaining, nowMs, paused) {
        if (!(nativeRemaining >= 0)) {
            return null;
        }
        if (state.credits === undefined) {
            state.credits = nativeRemaining > NATIVE_UP_NEXT_SECONDS;
            state.total = state.credits ? NEXT_EPISODE_COUNTDOWN_SECONDS : Math.max(nativeRemaining, 1);
            state.deadline = nowMs + NEXT_EPISODE_COUNTDOWN_SECONDS * 1000;
            state.last = nowMs;
        }
        if (paused && state.credits) {
            state.deadline += nowMs - state.last;
        }
        state.last = nowMs;
        var seconds = nativeRemaining;
        if (state.credits) {
            seconds = Math.min(nativeRemaining, Math.max(0, Math.ceil((state.deadline - nowMs) / 1000)));
        } else if (nativeRemaining > state.total) {
            state.total = nativeRemaining;
        }
        var start = state.credits && seconds <= 0 && !state.started;
        if (start) {
            state.started = true;
        }
        return {
            seconds: seconds,
            progress: Math.round((1 - seconds / state.total) * 1000) / 10,
            start: start
        };
    }

    function watchNextEpisodeCountdown() {
        if (!window.getComputedStyle || !document.documentElement ||
            !getComputedStyle(document.documentElement).getPropertyValue('--mediarow-player-skin').trim()) {
            return;
        }
        var state = {};
        var observer = null;
        var shown = null;

        function showSeconds(text) {
            var countdown = text.querySelector('.upNextDialog-countdownText');
            var match = countdown && /\d+/.exec(countdown.textContent || '');
            if (match && shown !== null && parseInt(match[0], 10) !== shown) {
                countdown.textContent = countdown.textContent.replace(/\d+/, String(shown));
            }
        }

        function reset() {
            state = {};
            shown = null;
            if (observer) {
                observer.disconnect();
                observer = null;
            }
        }

        setInterval(function () {
            var dialog = document.querySelector('.upNextContainer:not(.hide):not(.upNextDialog-hidden)');
            var button = dialog && dialog.querySelector('.btnStartNow');
            var text = dialog && dialog.querySelector('.upNextDialog-nextVideoText');
            if (!button || !text) {
                reset();
                return;
            }
            var native = NaN;
            if (state.credits) {
                native = Infinity;
            } else {
                var match = /\d+/.exec((text.querySelector('.upNextDialog-countdownText') || {}).textContent || '');
                native = match ? parseInt(match[0], 10) : NaN;
            }
            var video = document.querySelector('video');
            var result = nextEpisodeCountdown(state, native, Date.now(), !!(video && video.paused));
            button.style.setProperty('--mediarow-next-progress', result === null ? '100%' : result.progress + '%');
            if (!result || !state.credits) {
                return;
            }
            shown = result.seconds;
            if (!observer && window.MutationObserver) {
                // Jellyfin rewrites its countdown text every 400 ms; replace it before it is painted.
                observer = new MutationObserver(function () {
                    showSeconds(text);
                });
                observer.observe(text, { childList: true, subtree: true, characterData: true });
            }
            showSeconds(text);
            if (result.start) {
                button.click();
            }
        }, 250);
    }

    try {
        watchNextEpisodeCountdown();
    } catch (error) {
        console.debug('Mediarow: player skin unavailable', error);
    }

    var attempts = 0;
    var retryTimer = setInterval(function () {
        attempts++;
        loadRows();
        if (document.querySelector('[data-mediarow-home="true"]') || attempts >= 30) {
            clearInterval(retryTimer);
        }
    }, 1000);

    if (window.MutationObserver) {
        var observer = new MutationObserver(function () {
            moveRecentlyAddedBelow(document.querySelector('[data-mediarow-home="true"]'));
            loadRows();
        });
        observer.observe(document.documentElement, { childList: true, subtree: true });
    }
    window.addEventListener('hashchange', function () {
        loadVersion++;
        isLoading = false;
        var root = document.querySelector('[data-mediarow-home="true"]');
        if (root) {
            root.remove();
        }
        loadRows();
    });
    setTimeout(loadRows, 500);
}());
