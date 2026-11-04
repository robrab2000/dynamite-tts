//! The text around the highlight on Linux, through AT-SPI (the accessibility bus), for Explain
//! and ELI5.
//!
//! A listener remembers the last object that reported `object:text-selection-changed`. When the
//! shortcut runs, that object's text (and its parents', for more of the page) is read and used if
//! it contains the highlight. Chromium and Electron put U+FFFC in place of each child object in
//! their text, so those are filled in from the children through the Hypertext interface.
//!
//! Chromium and Electron only expose their text when `org.a11y.Status.ScreenReaderEnabled` is
//! set, so it is turned on while a mode that uses context is selected, and put back afterwards
//! if this app turned it on. GTK apps expose their text either way.

use std::sync::mpsc;
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

use zbus::blocking::{Connection, MessageIterator, Proxy, proxy};
use zbus::proxy::CacheProperties;
use zbus::zvariant::{OwnedObjectPath, OwnedValue, Structure};
use zbus::{MatchRule, message};

use crate::log;

const EMBEDDED: char = '\u{fffc}';
/// Gives up on a lookup after this long, so a slow app can't hold up the shortcut.
const LOOKUP_TIMEOUT: Duration = Duration::from_millis(700);
/// A selection event older than this is not trusted to be the current highlight.
const MAX_EVENT_AGE: Duration = Duration::from_secs(600);
/// Stops filling in child objects past this much text or this many objects.
const MAX_FIELD_CHARS: usize = 6000;
const MAX_NODES: usize = 400;
/// Parents climbed above the object that reported the selection, for neighbouring paragraphs.
const MAX_PARENTS: usize = 3;
const PARAGRAPH: u32 = 4;
/// Roles whose text starts on its own line when filled into a parent's text.
const BLOCK_ROLES: &[&str] = &[
    "paragraph",
    "heading",
    "list item",
    "list",
    "section",
    "block quote",
    "article",
    "table",
    "table row",
    "table cell",
    "form",
    "description list",
    "description term",
    "description value",
];

type Object = (String, OwnedObjectPath);

#[derive(Default)]
struct State {
    listening: bool,
    /// The last object that reported a selection change, and when.
    last: Option<(Object, Instant)>,
    /// Whether this app turned `ScreenReaderEnabled` on, so it should turn it off again.
    enabled_by_us: bool,
}

fn state() -> &'static Mutex<State> {
    static STATE: OnceLock<Mutex<State>> = OnceLock::new();
    STATE.get_or_init(Mutex::default)
}

fn lock() -> std::sync::MutexGuard<'static, State> {
    state().lock().unwrap_or_else(|e| e.into_inner())
}

/// Runs `f` on its own thread; `None` if it fails or takes longer than `timeout`.
fn with_timeout<T: Send + 'static>(timeout: Duration, f: impl FnOnce() -> Option<T> + Send + 'static) -> Option<T> {
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let _ = tx.send(f());
    });
    rx.recv_timeout(timeout).ok().flatten()
}

fn status_proxy(session: &Connection) -> zbus::Result<Proxy<'static>> {
    proxy::Builder::new(session)
        .destination("org.a11y.Bus")?
        .path("/org/a11y/bus")?
        .interface("org.a11y.Status")?
        .cache_properties(CacheProperties::No)
        .build()
}

/// Turns context capture on or off. Blocks for at most a second; safe to call repeatedly.
pub fn watch(enabled: bool) {
    let _ = with_timeout(Duration::from_secs(1), move || {
        set_screen_reader_flag(enabled);
        Some(())
    });
    if enabled {
        start_listener();
    }
}

fn set_screen_reader_flag(enabled: bool) {
    let result = (|| -> zbus::Result<()> {
        let session = Connection::session()?;
        let status = status_proxy(&session)?;
        let mut st = lock();
        if enabled && !st.enabled_by_us {
            if !status.get_property::<bool>("ScreenReaderEnabled")? {
                status.set_property("ScreenReaderEnabled", true).map_err(zbus::Error::from)?;
                st.enabled_by_us = true;
                log!("context: turned on accessibility for Chromium and Electron apps");
            }
        } else if !enabled && st.enabled_by_us {
            status.set_property("ScreenReaderEnabled", false).map_err(zbus::Error::from)?;
            st.enabled_by_us = false;
            log!("context: turned accessibility back off");
        }
        Ok(())
    })();
    if let Err(e) = result {
        log!("context: could not change the accessibility setting: {e}");
    }
}

fn a11y_bus() -> zbus::Result<Connection> {
    let session = Connection::session()?;
    let address: String = session
        .call_method(Some("org.a11y.Bus"), "/org/a11y/bus", Some("org.a11y.Bus"), "GetAddress", &())?
        .body()
        .deserialize()?;
    zbus::blocking::connection::Builder::address(address.as_str())?.build()
}

fn start_listener() {
    {
        let mut st = lock();
        if st.listening {
            return;
        }
        st.listening = true;
    }
    std::thread::spawn(|| {
        if let Err(e) = listen() {
            log!("context: selection listener stopped: {e}");
        }
        lock().listening = false;
    });
}

fn listen() -> zbus::Result<()> {
    let bus = a11y_bus()?;
    let rule = MatchRule::builder()
        .msg_type(message::Type::Signal)
        .interface("org.a11y.atspi.Event.Object")?
        .member("TextSelectionChanged")?
        .build();
    let events = MessageIterator::for_match_rule(rule, &bus, Some(64))?;
    // Toolkits only send events someone has asked the registry for.
    let event = "object:text-selection-changed";
    let registry = ("org.a11y.atspi.Registry", "/org/a11y/atspi/registry", "org.a11y.atspi.Registry");
    if bus
        .call_method(
            Some(registry.0),
            registry.1,
            Some(registry.2),
            "RegisterEvent",
            &(event, Vec::<String>::new(), ""),
        )
        .is_err()
    {
        bus.call_method(Some(registry.0), registry.1, Some(registry.2), "RegisterEvent", &(event,))?;
    }
    log!("context: listening for selections");
    for msg in events {
        let msg = msg?;
        let header = msg.header();
        if let (Some(sender), Some(path)) = (header.sender(), header.path()) {
            lock().last = Some(((sender.to_string(), path.clone().into()), Instant::now()));
        }
    }
    Ok(())
}

/// The text around `selection`, from the object that last reported a selection, if that object's
/// text contains it.
pub fn context(selection: &str) -> Option<String> {
    let (object, at) = lock().last.clone()?;
    if at.elapsed() > MAX_EVENT_AGE {
        return None;
    }
    let selection = selection.trim().to_string();
    let started = Instant::now();
    let found = with_timeout(LOOKUP_TIMEOUT, move || {
        let bus = a11y_bus().ok()?;
        surrounding(&bus, object, &selection)
    });
    log!(
        "context: {} in {} ms",
        found.as_ref().map_or("none".to_string(), |c| format!("{} chars", c.chars().count())),
        started.elapsed().as_millis()
    );
    found
}

fn proxy<'a>(bus: &'a Connection, object: &'a Object, interface: &'static str) -> zbus::Result<Proxy<'a>> {
    proxy::Builder::new(bus)
        .destination(object.0.as_str())?
        .path(object.1.as_ref())?
        .interface(interface)?
        .cache_properties(CacheProperties::No)
        .build()
}

fn full_text(bus: &Connection, object: &Object) -> Option<String> {
    proxy(bus, object, "org.a11y.atspi.Text").ok()?.call("GetText", &(0i32, -1i32)).ok()
}

fn parent(bus: &Connection, object: &Object) -> Option<Object> {
    let props = proxy(bus, object, "org.freedesktop.DBus.Properties").ok()?;
    let value: OwnedValue = props.call("Get", &("org.a11y.atspi.Accessible", "Parent")).ok()?;
    let fields = Structure::try_from(value).ok()?.into_fields();
    let [name, path] = <[_; 2]>::try_from(fields).ok()?;
    let name = String::try_from(name).ok()?;
    let path = OwnedObjectPath::try_from(path).ok()?;
    // The desktop root and null objects end the climb.
    (path.as_str() != "/org/a11y/atspi/null" && path.as_str() != "/org/a11y/atspi/accessible/root")
        .then_some((name, path))
}

fn role_name(bus: &Connection, object: &Object) -> String {
    proxy(bus, object, "org.a11y.atspi.Accessible")
        .and_then(|p| p.call::<_, _, String>("GetRoleName", &()))
        .unwrap_or_default()
}

/// The child object that the U+FFFC at `offset` stands for.
fn embedded_child(bus: &Connection, object: &Object, offset: i32) -> Option<Object> {
    let hypertext = proxy(bus, object, "org.a11y.atspi.Hypertext").ok()?;
    let index: i32 = hypertext.call("GetLinkIndex", &(offset,)).ok()?;
    if index < 0 {
        return None;
    }
    let link: Object = hypertext.call("GetLink", &(index,)).ok()?;
    // Chromium's link is the child itself; other toolkits hand back the child from the link.
    let child = proxy(bus, &link, "org.a11y.atspi.Hyperlink")
        .ok()
        .and_then(|p| p.call::<_, _, Object>("GetObject", &(0i32,)).ok());
    Some(child.unwrap_or(link))
}

struct Budget {
    nodes: usize,
    chars: usize,
}

/// `object`'s text with every U+FFFC replaced by its child's text, or `None` past the budget.
fn expanded(bus: &Connection, object: &Object, budget: &mut Budget, depth: usize) -> Option<String> {
    budget.nodes += 1;
    if budget.nodes > MAX_NODES {
        return None;
    }
    let text = full_text(bus, object)?;
    if !text.contains(EMBEDDED) {
        budget.chars += text.chars().count();
        return (budget.chars <= MAX_FIELD_CHARS || depth == 0).then_some(text);
    }
    let mut out = String::new();
    for (offset, c) in text.chars().enumerate() {
        if c != EMBEDDED {
            out.push(c);
            budget.chars += 1;
            continue;
        }
        let Some(child) = embedded_child(bus, object, offset as i32) else { continue };
        if depth >= 12 {
            continue;
        }
        let inner = expanded(bus, &child, budget, depth + 1)?;
        if BLOCK_ROLES.contains(&role_name(bus, &child).as_str()) {
            out.push('\n');
            out.push_str(inner.trim());
            out.push('\n');
        } else {
            out.push_str(&inner);
        }
    }
    (budget.chars <= MAX_FIELD_CHARS).then_some(out)
}

fn tidy(text: &str) -> String {
    let lines: Vec<String> = text.lines().map(|l| l.split_whitespace().collect::<Vec<_>>().join(" ")).collect();
    let joined = lines.join("\n");
    let mut out = String::new();
    for line in joined.split('\n') {
        if line.is_empty() && out.ends_with("\n\n") {
            continue;
        }
        out.push_str(line);
        out.push('\n');
    }
    out.trim().to_string()
}

fn contains_loosely(text: &str, selection: &str) -> bool {
    let squash = |s: &str| s.split_whitespace().collect::<Vec<_>>().join(" ");
    squash(text).contains(&squash(selection))
}

/// The widest text around the selection that stays within the budget: the reporting object,
/// then up to [`MAX_PARENTS`] of its parents.
fn surrounding(bus: &Connection, object: Object, selection: &str) -> Option<String> {
    let mut best: Option<String> = None;
    let mut current = Some(object);
    for _ in 0..=MAX_PARENTS {
        let Some(node) = current.take() else { break };
        let mut budget = Budget { nodes: 0, chars: 0 };
        let Some(text) = expanded(bus, &node, &mut budget, 0).map(|t| tidy(&t)) else {
            if best.is_none() {
                return paragraph_fallback(bus, &node, selection);
            }
            break;
        };
        if !contains_loosely(&text, selection) {
            // The reporting object isn't where the highlight is: a stale event from another app.
            if best.is_none() {
                return paragraph_fallback(bus, &node, selection);
            }
            break;
        }
        best = Some(text);
        if matches!(role_name(bus, &node).as_str(), "document web" | "document frame" | "frame" | "application") {
            break;
        }
        current = parent(bus, &node);
    }
    best
}

/// For an object whose whole text doesn't match (a huge editor buffer cut by the budget), the
/// paragraph at its selection.
fn paragraph_fallback(bus: &Connection, object: &Object, selection: &str) -> Option<String> {
    let text = proxy(bus, object, "org.a11y.atspi.Text").ok()?;
    let (start, _end): (i32, i32) = text.call("GetSelection", &(0i32,)).ok()?;
    let (para, _, _): (String, i32, i32) = text.call("GetStringAtOffset", &(start, PARAGRAPH)).ok()?;
    let para = tidy(&para.replace(EMBEDDED, ""));
    contains_loosely(&para, selection).then_some(para)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tidies_whitespace_and_blank_lines() {
        assert_eq!(tidy("  a   b \n\n\n\n c\t d  "), "a b\n\nc d");
        assert_eq!(tidy("\nOne\n\nTwo\n"), "One\n\nTwo");
    }

    #[test]
    fn matches_across_whitespace() {
        assert!(contains_loosely("the  Lemonade\nserver", "Lemonade server"));
        assert!(!contains_loosely("a drink", "Lemonade"));
    }
}
