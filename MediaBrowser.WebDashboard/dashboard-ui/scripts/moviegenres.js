﻿define([], function () {

    return function (view, params, tabContent) {

        var self = this;

        var data = {};
        function getPageData() {
            var key = getSavedQueryKey();
            var pageData = data[key];

            if (!pageData) {
                pageData = data[key] = {
                    query: {
                        SortBy: "SortName",
                        SortOrder: "Ascending",
                        IncludeItemTypes: "Movie",
                        Recursive: true,
                        Fields: "DateCreated,SyncInfo,ItemCounts",
                        StartIndex: 0
                    },
                    view: LibraryBrowser.getSavedView(key) || LibraryBrowser.getDefaultItemsView('Thumb', 'Thumb')
                };

                pageData.query.ParentId = params.topParentId;
                LibraryBrowser.loadSavedQueryValues(key, pageData.query);
            }
            return pageData;
        }

        function getQuery() {

            return getPageData().query;
        }

        function getSavedQueryKey() {

            return LibraryBrowser.getSavedQueryKey('genres');
        }

        function getPromise() {

            Dashboard.showLoadingMsg();
            var query = getQuery();

            return ApiClient.getGenres(Dashboard.getCurrentUserId(), query);
        }

        function reloadItems(context, promise) {

            var query = getQuery();

            promise.then(function (result) {

                var html = '';

                var viewStyle = self.getCurrentViewStyle();

                if (viewStyle == "Thumb") {
                    html = LibraryBrowser.getPosterViewHtml({
                        items: result.Items,
                        shape: "backdrop",
                        preferThumb: true,
                        context: 'movies',
                        showItemCounts: true,
                        centerText: true,
                        lazy: true,
                        overlayMoreButton: true
                    });
                }
                else if (viewStyle == "ThumbCard") {

                    html = LibraryBrowser.getPosterViewHtml({
                        items: result.Items,
                        shape: "backdrop",
                        preferThumb: true,
                        context: 'movies',
                        showItemCounts: true,
                        cardLayout: true,
                        showTitle: true,
                        lazy: true
                    });
                }
                else if (viewStyle == "PosterCard") {
                    html = LibraryBrowser.getPosterViewHtml({
                        items: result.Items,
                        shape: "portrait",
                        context: 'movies',
                        showItemCounts: true,
                        lazy: true,
                        cardLayout: true,
                        showTitle: true
                    });
                }
                else if (viewStyle == "Poster") {
                    html = LibraryBrowser.getPosterViewHtml({
                        items: result.Items,
                        shape: "portrait",
                        context: 'movies',
                        centerText: true,
                        showItemCounts: true,
                        lazy: true,
                        overlayMoreButton: true
                    });
                }

                var elem = context.querySelector('#items');
                elem.innerHTML = html;
                ImageLoader.lazyChildren(elem);

                LibraryBrowser.saveQueryValues(getSavedQueryKey(), query);

                Dashboard.hideLoadingMsg();
            });
        }
        self.getViewStyles = function () {
            return 'Poster,PosterCard,Thumb,ThumbCard'.split(',');
        };

        self.getCurrentViewStyle = function () {
            return getPageData(tabContent).view;
        };

        self.setCurrentViewStyle = function (viewStyle) {
            getPageData(tabContent).view = viewStyle;
            LibraryBrowser.saveViewSetting(getSavedQueryKey(tabContent), viewStyle);
            fullyReload();
        };

        self.enableViewSelection = true;
        var promise;

        self.preRender = function () {
            promise = getPromise();
        };

        self.renderTab = function () {

            reloadItems(tabContent, promise);
        };

        function fullyReload() {
            self.preRender();
            self.renderTab();
        }

        var btnSelectView = tabContent.querySelector('.btnSelectView');
        btnSelectView.addEventListener('click', function (e) {

            LibraryBrowser.showLayoutMenu(e.target, self.getCurrentViewStyle(), self.getViewStyles());
        });

        btnSelectView.addEventListener('layoutchange', function (e) {

            self.setCurrentViewStyle(e.detail.viewStyle);
        });
    };
});