define(['loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-scroller', 'emby-select'], function (loading) {
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
        this.view = view;
        view.querySelector('form').addEventListener('submit', onSubmit.bind(this));
        view.addEventListener('viewshow', onResume.bind(this));
        view.addEventListener('viewbeforehide', onPause.bind(this));
    }

    function onResume() {
        var instance = this;
        var epoch = instance.viewEpoch = (instance.viewEpoch || 0) + 1;
        instance.loadingConfig = true;

        loading.show();

        return ApiClient.getPluginConfiguration("10b82431-f939-4507-a8e2-9509dd3fd6ae").then(function (config) {
            if (epoch !== instance.viewEpoch) return;

            var view = instance.view;

            view.querySelector('.txtUsername').value = config.Username || "";
            view.querySelector('.txtLatLon').value = config.LatLon || "";
        }).catch(function (error) {
            if (epoch !== instance.viewEpoch) return;
            console.error('Unable to load TuneIn configuration.', error);
            Dashboard.alert('Unable to load TuneIn configuration.');
        }).then(function () {
            if (epoch === instance.viewEpoch) {
                instance.loadingConfig = false;
                loading.hide();
            }
        });
    }

    function onPause() {
        this.viewEpoch = (this.viewEpoch || 0) + 1;
        if (this.loadingConfig) {
            this.loadingConfig = false;
            loading.hide();
        }
    }

    return View;
});
