#!/bin/bash
# Takes a game through what Android does to an app, on an emulator or a device, and checks after each step that it's
# in front, in the process it should be, still drawing, and with no crash or ANR.
#
# Usage: test/android/lifecycle.sh <apk> <package> [screenshot folder]
#
# The game has to be animating on the screen it opens on, as "still drawing" means two screenshots differ. It uses
# the device adb finds, or ANDROID_SERIAL. It locks the device's rotation, and frees it again at the end.
# Exits nonzero if a check fails.

APK=$1
PACKAGE=$2
OUT=${3:-}
ADB=adb
FAILURES=0
SETTLE=4

[ -n "$OUT" ] && mkdir -p "$OUT"

pid_of() { $ADB shell pidof "$PACKAGE" | tr -d '\r'; }

# The launcher activity, started as the launcher would (monkey can't start one on some Android 11 images)
launch() {
    local activity
    activity=$($ADB shell cmd package resolve-activity --brief -c android.intent.category.LAUNCHER "$PACKAGE" | tail -1 | tr -d '\r')
    $ADB shell am start -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -n "$activity" > /dev/null 2>&1
}

home() { $ADB shell input keyevent KEYCODE_HOME; }


restore_settings() {
    $ADB shell wm user-rotation free
}

# What the display is turned to: 0 is its natural way up, and 1 a quarter turn
display_rotation() {
    $ADB shell dumpsys window displays | grep -m1 -oE 'mCurrentRotation=ROTATION_[0-9]+' | sed -E 's/.*ROTATION_([0-9]+)/\1/'
}
trap restore_settings EXIT

check() {
    if [ "$2" = "$3" ]; then
        echo "ok   $1"
    else
        echo "FAIL $1: expected '$3', got '$2'"
        FAILURES=$((FAILURES + 1))
    fi
}

is_in_front() {
    if $ADB shell dumpsys activity activities | grep -E 'ResumedActivity|topResumedActivity' | grep -q "$PACKAGE"; then
        echo yes
    else
        echo no
    fi
}

is_drawing() {
    local first second
    first=$($ADB exec-out screencap -p | cksum)
    sleep 1
    second=$($ADB exec-out screencap -p | cksum)
    [ "$first" != "$second" ] && echo yes || echo no
}

# Forgets the log, so that each step answers for its own crashes
begin_step() {
    echo "--- $1"
    $ADB logcat -b all -c
}

# Whether Android's own log says this happened to the game's activity: so a step can't pass by not happening
log_has() {
    $ADB logcat -d -b events | grep "$1" | grep -q "$PACKAGE" && echo yes || echo no
}

# What should hold after a step that leaves the game in front
check_running() {
    local name=$1
    sleep "$SETTLE"
    check "$name: in front" "$(is_in_front)" yes
    check "$name: drawing" "$(is_drawing)" yes
    check "$name: no crash" "$($ADB logcat -d -b crash | grep -c "$PACKAGE")" 0
    check "$name: no ANR" "$($ADB logcat -d | grep -c "ANR in $PACKAGE")" 0
    check "$name: no fatal error" "$($ADB logcat -d '*:E' | grep -cE 'FATAL EXCEPTION|\[FATAL\]|Fatal signal')" 0
    if [ -n "$OUT" ]; then
        $ADB exec-out screencap -p > "$OUT/$(echo "$name" | tr ' ' '-').png"
    fi
}

$ADB shell wm user-rotation lock 0
$ADB install -r -g "$APK" > /dev/null
$ADB shell am force-stop "$PACKAGE"
$ADB shell input keyevent KEYCODE_WAKEUP

begin_step "cold start"
launch
sleep 6
check_running "cold start"
PID=$(pid_of)
check "cold start: has a process" "$([ -n "$PID" ] && echo yes || echo no)" yes

begin_step "home and back"
home
sleep 3
launch
check_running "home and back"
check "home and back: same process" "$(pid_of)" "$PID"

begin_step "home and back five times quickly"
for _ in 1 2 3 4 5; do
    home
    sleep 0.5
    launch
    sleep 0.5
done
check_running "quick home and back"
check "quick home and back: same process" "$(pid_of)" "$PID"

begin_step "rotate"
$ADB shell wm user-rotation lock 1
check_running "rotated"
check "rotated: the display really turned" "$(display_rotation)" 90
check "rotated: same process" "$(pid_of)" "$PID"
check "rotated: the activity wasn't relaunched" "$(log_has wm_relaunch)" no
check "rotated: the activity wasn't destroyed" "$(log_has wm_destroy_activity)" no
$ADB shell wm user-rotation lock 0
check_running "rotated back"
check "rotated back: the display really turned" "$(display_rotation)" 0
check "rotated back: same process" "$(pid_of)" "$PID"

begin_step "screen off and on"
$ADB shell input keyevent KEYCODE_SLEEP
sleep 3
$ADB shell input keyevent KEYCODE_WAKEUP
$ADB shell wm dismiss-keyguard
check_running "screen off and on"
check "screen off and on: same process" "$(pid_of)" "$PID"

begin_step "memory pressure in the background"
home
sleep 2
$ADB shell am send-trim-memory "$PACKAGE" COMPLETE
sleep 2
launch
check_running "memory pressure"
check "memory pressure: same process" "$(pid_of)" "$PID"

begin_step "killed in the background"
home
sleep 2
$ADB shell am kill "$PACKAGE"
sleep 1
check "killed: process gone" "$(pid_of)" ""
launch
sleep 6
check_running "after being killed"
PID=$(pid_of)

begin_step "back button: the activity is destroyed, then started again"
$ADB shell input keyevent KEYCODE_BACK
sleep 3
check "back: the activity finished" "$(log_has wm_finish_activity)" yes
check "back: the activity was destroyed" "$(log_has wm_destroy_activity)" yes

# NOTE: A game that sets ExitProcessOnFinish has no process now. One that doesn't starts its new
# activity and render thread in the process it's still in, which is the case worth trying
AFTER_BACK=$(pid_of)
if [ -n "$AFTER_BACK" ]; then
    echo "     the process stayed, so this is a relaunch in the same process"
fi
launch
sleep 6
check_running "after back"
if [ -n "$AFTER_BACK" ]; then
    check "after back: same process" "$(pid_of)" "$AFTER_BACK"
fi

begin_step "force stop, then start again"
$ADB shell am force-stop "$PACKAGE"
sleep 1
check "force stop: process gone" "$(pid_of)" ""
launch
sleep 6
check_running "after force stop"

$ADB shell am force-stop "$PACKAGE"

echo
if [ "$FAILURES" -eq 0 ]; then
    echo "All passed"
else
    echo "$FAILURES failed"
    exit 1
fi
