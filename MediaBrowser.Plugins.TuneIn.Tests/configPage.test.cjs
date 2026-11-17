'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const configDir = path.join(__dirname, '..', 'MediaBrowser.Plugins.TuneIn', 'Configuration');
const pluginId = '10b82431-f939-4507-a8e2-9509dd3fd6ae';
const settle = () => new Promise(resolve => setImmediate(resolve));

function deferred() {
    let resolve, reject;
    const promise = new Promise((ok, fail) => { resolve = ok; reject = fail; });
    return { promise, resolve, reject };
}

function harness() {
    const handlers = {};
    const formHandlers = {};
    const username = { value: '' };
    const latLon = { value: '' };
    const form = { addEventListener: (event, handler) => { formHandlers[event] = handler; } };
    const elements = { form, '.txtUsername': username, '.txtLatLon': latLon };
    const view = {
        addEventListener(event, handler) { handlers[event] = handler; },
        querySelector(selector) { return elements[selector]; }
    };
    const reads = [];
    const writes = [];
    const notices = [];
    const errors = [];
    const loader = { shown: 0, hidden: 0, show() { this.shown++; }, hide() { this.hidden++; } };
    const ApiClient = {
        getPluginConfiguration(id) {
            assert.equal(id, pluginId);
            const request = deferred();
            reads.push(request);
            return request.promise;
        },
        updatePluginConfiguration(id, config) {
            writes.push({ id, config });
            return Promise.resolve('saved');
        }
    };
    const Dashboard = {
        alert(message) { notices.push(message); },
        processPluginConfigurationUpdateResult(result) { notices.push(result); }
    };
    let PluginView;
    vm.runInNewContext(fs.readFileSync(path.join(configDir, 'configPage.js'), 'utf8'), {
        define(dependencies, factory) {
            assert.equal(dependencies[0], 'loading');
            assert.equal(dependencies.includes('baseView'), false);
            PluginView = factory(loader);
        },
        ApiClient, Dashboard, console: { error(...args) { errors.push(args); } }
    });

    function BaseView(element) { this.view = element; }
    BaseView.prototype.onResume = function () { return handlers.viewshow(); };
    BaseView.prototype.onPause = function () { return handlers.viewbeforehide(); };
    BaseView.prototype.onBeginResume = function () { return 'native transition'; };
    function Adapter(element) { BaseView.call(this, element); PluginView.call(this, element); }
    Object.assign(Adapter.prototype, BaseView.prototype, PluginView.prototype);
    return { instance: new Adapter(view), username, latLon, formHandlers, reads, writes, notices, errors, loader };
}

test('native view lifecycle loads labeled upstream settings on each visit', async () => {
    const html = fs.readFileSync(path.join(configDir, 'configPage.html'), 'utf8');
    assert.match(html, /data-controller="__plugin\/tuneinjs"/);
    assert.match(html, /class="txtUsername"[^>]*label="Your Tune In username:"/);
    assert.match(html, /class="txtLatLon"[^>]*label="Your LatLon:"/);
    const app = harness();
    assert.equal(app.instance.onBeginResume(), 'native transition');
    const first = app.instance.onResume();
    app.reads[0].resolve({ Username: 'listener', LatLon: '1,2' });
    await first;
    assert.equal(app.username.value, 'listener');
    assert.equal(app.latLon.value, '1,2');
    app.instance.onPause();
    const second = app.instance.onResume();
    app.reads[1].resolve({ Username: null, LatLon: null });
    await second;
    assert.equal(app.username.value, '');
    assert.equal(app.latLon.value, '');
    assert.equal(app.loader.shown, 2);
    assert.equal(app.loader.hidden, 2);
});

test('save retains unrelated configuration and converts empty fields to null', async () => {
    const app = harness();
    app.username.value = 'listener';
    app.latLon.value = '';
    let prevented = false;
    assert.equal(app.formHandlers.submit({ preventDefault() { prevented = true; } }), false);
    assert.equal(prevented, true);
    app.reads[0].resolve({ Username: 'old', LatLon: '1,2', Other: 42 });
    await settle();
    assert.deepEqual(app.writes, [{ id: pluginId, config: { Username: 'listener', LatLon: null, Other: 42 } }]);
    assert.deepEqual(app.notices, ['saved']);
});

test('navigation discards stale loads without hiding the next visit loader', async () => {
    const app = harness();
    const first = app.instance.onResume();
    app.instance.onPause();
    assert.equal(app.loader.hidden, 1);
    const second = app.instance.onResume();
    app.reads[0].resolve({ Username: 'stale', LatLon: 'stale' });
    await first;
    assert.equal(app.username.value, '');
    assert.equal(app.loader.hidden, 1);
    app.reads[1].resolve({ Username: 'current', LatLon: '3,4' });
    await second;
    assert.equal(app.username.value, 'current');
    assert.equal(app.latLon.value, '3,4');
    assert.equal(app.loader.hidden, 2);
});

test('a failed active load reports the error and releases the loader', async () => {
    const app = harness();
    const loading = app.instance.onResume();
    app.reads[0].reject(new Error('offline'));
    await loading;
    assert.equal(app.loader.hidden, 1);
    assert.deepEqual(app.notices, ['Unable to load TuneIn configuration.']);
    assert.equal(app.errors.length, 1);
});

test('a failed stale load does not alert or disturb a later visit', async () => {
    const app = harness();
    const first = app.instance.onResume();
    app.instance.onPause();
    const second = app.instance.onResume();
    app.reads[0].reject(new Error('offline'));
    await first;
    assert.deepEqual(app.notices, []);
    assert.equal(app.errors.length, 0);
    assert.equal(app.loader.hidden, 1);
    app.reads[1].resolve({ Username: 'current', LatLon: '' });
    await second;
    assert.equal(app.username.value, 'current');
    assert.equal(app.loader.hidden, 2);
});
