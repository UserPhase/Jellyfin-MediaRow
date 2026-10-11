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
            } else if (isContinueWatching) {
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
                    DisplayTitle: isContinueWatching || !category || isAnime
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

    function resolvePlaybackItem(apiClient, itemId) {
        var url = apiClient.getUrl('Mediarow/Playback/Resolve/' + encodeURIComponent(itemId));
        if (apiClient.ajax) {
            return apiClient.ajax({
                url: url,
                type: 'POST',
                data: '{}',
                contentType: 'application/json; charset=utf-8',
                dataType: 'json'
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
            method: 'POST',
            credentials: 'same-origin',
            headers: headers
        }).then(function (response) {
            if (!response.ok) {
                return response.text().then(function (body) {
                    var error = new Error('Resolve failed: ' + response.status);
                    error.status = response.status;
                    error.responseText = body;
                    throw error;
                });
            }
            return response.json();
        });
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

    function createCard(apiClient, item, index) {
        var card = document.createElement('a');
        card.className = 'mediarowHomeCard';
        card.href = '#/details?id=' + encodeURIComponent(item.Id);
        card.title = item.Name || '';
        card.draggable = false;
        card.setAttribute('data-mediarow-index', String(index));
        card.addEventListener('click', function (event) { openResolvedDetails(event, apiClient, item); });

        var image = document.createElement('img');
        image.className = 'mediarowHomeCardImage';
        image.setAttribute('loading', 'lazy');
        image.alt = item.Name || '';
        image.draggable = false;
        image.src = apiClient.getUrl('Mediarow/Items/' + item.Id + '/Images/Primary');
        card.appendChild(image);

        var title = document.createElement('div');
        title.className = 'mediarowHomeCardTitle';
        title.textContent = item.Name || '';
        card.appendChild(title);
        return card;
    }

    function setupCarousel(apiClient, items, catalogItems, previousButton, nextButton) {
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
                        var card = createCard(apiClient, item, index);
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
        header.appendChild(controls);
        row.appendChild(header);

        var items = document.createElement('div');
        items.className = 'mediarowHomeRowItems';
        items.setAttribute('role', 'group');
        items.setAttribute('aria-label', section.DisplayTitle + ' carousel. Drag, swipe or use the arrow keys to browse.');
        (result.Items || []).forEach(function (item, index) {
            items.appendChild(createCard(apiClient, item, index));
        });
        if (items.children.length > 0) {
            row.appendChild(items);
            setupCarousel(apiClient, items, result.Items, previousButton, nextButton);
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
                root.appendChild(filters);
                var status = document.createElement('p');
                status.className = 'mediarowHomeStatus';
                status.setAttribute('role', 'status');
                status.textContent = sections.length ? 'Loading ' + selectedCategory.toLowerCase() + ' rows...' : 'No content selected. Enable content categories and rows in Mediarow settings.';
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
