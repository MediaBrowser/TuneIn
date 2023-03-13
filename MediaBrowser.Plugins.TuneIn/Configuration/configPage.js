define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-scroller', 'emby-select'], function (BaseView, loading) {
    'use strict';

    function onSubmit(e) {

        e.preventDefault();

        var instance = this;
        var form = this.view;

        loading.show();

        ApiClient.getPluginConfiguration("10b82431-f939-4507-a8e2-9509dd3fd6ae").then(function (config) {

            var Username = form.querySelector('.txtUsername').value;
            var LatLon = form.querySelector('.txtLatLon').value;

            config.Username = Username ? Username : null;
            config.LatLon = LatLon ? LatLon : null;

            ApiClient.updatePluginConfiguration("10b82431-f939-4507-a8e2-9509dd3fd6ae", config).then(Dashboard.processPluginConfigurationUpdateResult);
        });

        // Disable default form submission
        return false;
    }

    function View(view, params) {
        BaseView.apply(this, arguments);

        view.querySelector('form').addEventListener('submit', onSubmit.bind(this));
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {

        BaseView.prototype.onResume.apply(this, arguments);

        var instance = this;

        loading.show();

        ApiClient.getPluginConfiguration("10b82431-f939-4507-a8e2-9509dd3fd6ae").then(function (config) {

            var view = instance.view;

            view.querySelector('.txtUsername').value = config.Username || "";
            view.querySelector('.txtLatLon').value = config.LatLon || "";

            loading.hide();
        });
    };

    return View;
});
