"use strict";
// Guards for the toast auto-dismiss schedules (wwwroot/js/owner-toast.js).
// The shipped file runs its DOM wiring on load; stub the calls it makes.
globalThis.document = { querySelectorAll: () => [], addEventListener: () => {} };
globalThis.window = {};

const assert = require("node:assert/strict");
const { describe, it } = require("node:test");
const toast = require("../../src/SboxNetworkStorage.Server/wwwroot/js/owner-toast.js");

describe("toast schedule", () => {
  it("dismisses after the full duration", () => {
    assert.equal(toast.DefaultDurationMs >= 5000, true);
    const schedule = toast.startSchedule(6000, 1000);
    assert.equal(toast.remainingMs(schedule, 1000), 6000);
    assert.equal(toast.remainingMs(schedule, 4000), 3000);
    assert.equal(toast.remainingMs(schedule, 7000), 0);
  });

  it("pauses while hovered or focused and resumes after", () => {
    const schedule = toast.startSchedule(6000, 0);
    toast.pauseSchedule(schedule, 2000);
    assert.equal(toast.remainingMs(schedule, 5000), 4000);
    assert.equal(toast.remainingMs(schedule, 9000), 4000);
    toast.resumeSchedule(schedule, 9000);
    assert.equal(toast.remainingMs(schedule, 11000), 2000);
    assert.equal(toast.remainingMs(schedule, 13000), 0);
  });

  it("tolerates double pause and resume without a pause", () => {
    const schedule = toast.startSchedule(6000, 0);
    toast.pauseSchedule(schedule, 1000);
    toast.pauseSchedule(schedule, 2000);
    assert.equal(toast.remainingMs(schedule, 9000), 5000);
    toast.resumeSchedule(schedule, 9000);
    toast.resumeSchedule(schedule, 9500);
    assert.equal(toast.remainingMs(schedule, 10000), 4000);
  });
});
