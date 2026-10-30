//! The **Catalog** window on the title screen: a button below the game's own Mods button opens a
//! window of mod cards, three a row, that you can sort, page through and click to install or remove.
//!
//! Everything is the game's own UI: the button is a clone of the Mods button, the window a clone of
//! the About window (a `SimpleWindow`, whose Back button returns to the main menu by itself), and
//! every card or tab is made by the game's `Button.create`, so it looks and sounds like the rest of
//! the menus. Cards sit at fixed places in the window rather than in a layout group, so their
//! three lines of text never push each other around. Objects are named `Mod_*`; the window is
//! filled again each time it opens (the catalog can change) and after a sort or page change.
//! Main thread only, like every UI callback here.

use crate::il2cpp::{self, Object};
use crate::modmenu::{children, clone, component, destroy, find, go_of, hook, navigation_items, set_text, ui_root};
use crate::unity::{self, R};
use crate::{log, symbols};
use jibbers_distribution::catalog::Package;
use jibbers_distribution::install::InstalledPackage;
use std::collections::BTreeMap;
use std::ffi::c_void;
use std::ptr::null_mut;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Mutex;

const BUTTON: &str = "Mod_CatalogButton";
const WINDOW: &str = "Mod_CatalogMenu";
const SORTS: &[&str] = &["Name", "Installed", "Updates", "Size"];
const COLUMNS: usize = 3;
const PER_PAGE: usize = 9;
// The grid, around the window's centre (canvas units; the title screen canvas is 1920x1080).
const CARD: [f32; 2] = [470.0, 168.0];
const GAP: f32 = 18.0;
const GRID_TOP: f32 = 300.0;

static SORT: AtomicUsize = AtomicUsize::new(0);
/// How far (canvas units, as f32 bits) the window sits from the screen's centre: the game puts its
/// windows beside the title logo. Everything is placed that much back, so the catalog is centred.
static SHIFT: Mutex<[f32; 2]> = Mutex::new([0.0, 0.0]);
static PAGE: AtomicUsize = AtomicUsize::new(0);
/// Package ids of the cards on the open page, by card index.
static CARDS: Mutex<Vec<String>> = Mutex::new(Vec::new());
/// What the window last showed: (catalog sequence, status text), to redraw only on a change.
static SHOWN: Mutex<(u64, String)> = Mutex::new((0, String::new()));

/// Adds the Catalog button and window to the title screen if it doesn't have them (Ok(true) when
/// built), and keeps an open window's status and cards current. Cheap otherwise.
pub fn ensure() -> R<bool> {
    unsafe {
        let Ok(ui) = ui_root() else { return Ok(false) };
        let list = find(ui, "MainMenu/Content/VList");
        if list.is_null() {
            return Ok(false);
        }
        if find(list, BUTTON).is_null() || find(ui, &format!("MainMenu/{WINDOW}")).is_null() {
            build(ui, list)?;
            return Ok(true);
        }
        let window = find(ui, &format!("MainMenu/{WINDOW}/Content"));
        if !window.is_null() && unity::call_unbox::<bool>(go_of(window)?, "get_activeInHierarchy").unwrap_or(false) {
            let snap = jibbers_distribution::manager::snapshot(&crate::game_dir());
            let now = (snap.catalog.as_ref().map(|c| c.sequence).unwrap_or(0), status_text());
            let changed = { let mut shown = SHOWN.lock().unwrap(); let c = *shown != now; *shown = now; c };
            if changed {
                fill(window)?;
            }
        }
        Ok(false)
    }
}

unsafe fn build(ui: *mut Object, list: *mut Object) -> R<()> {
    let menu = find(ui, "MainMenu");
    let mods = find(list, "ModsButton");
    let about = find(menu, "AboutMenu");
    if [menu, mods, about, find(about, "Content/VList/BackButton")].iter().any(|o| o.is_null()) {
        return Err("title screen layout changed (game update?)".into());
    }
    for c in children(list).into_iter().chain(children(menu)) {
        let n = unity::name(go_of(c)?);
        if n == BUTTON || n == WINDOW {
            destroy(c)?;
        }
    }

    // The game placed its buttons once, 140 apart; its list layout would squeeze them into the
    // list's box instead. Keep the span they cover (top of the first to bottom of the last).
    let rows = children(list);
    let edge = |c: *mut Object, top: bool| {
        let h = unity::call_unbox::<[f32; 4]>(c, "get_rect").map(|r| r[3]).unwrap_or(120.0);
        let y = unity::call_unbox::<[f32; 2]>(c, "get_anchoredPosition").map(|p| p[1]).unwrap_or(0.0);
        if top { y + h / 2.0 } else { y - h / 2.0 }
    };
    let (top, bottom) = match (rows.first(), rows.last()) {
        (Some(&f), Some(&l)) => (edge(f, true), edge(l, false)),
        _ => return Err("title list is empty".into()),
    };
    // The button, right below Mods.
    let button = clone(mods, list, BUTTON)?;
    set_text(button, "LabelText", "Catalog");
    let mut at = unity::call_unbox::<i32>(mods, "GetSiblingIndex")? + 1;
    unity::callv(button, "SetSiblingIndex", &mut [&mut at as *mut i32 as _])?;
    let widget = component(button, "Button")?;
    hook(widget, "System.Action<Button>", on_open as *const () as usize, 0)?;
    let rows = children(list);
    let (h, pitch) = title_rows(top, bottom, rows.len());
    for (i, c) in rows.iter().enumerate() {
        let mut size = unity::call_unbox::<[f32; 2]>(*c, "get_sizeDelta").unwrap_or([0.0, 120.0]);
        let mut at = unity::call_unbox::<[f32; 2]>(*c, "get_anchoredPosition").unwrap_or_default();
        size[1] = h;
        at[1] = top - h / 2.0 - i as f32 * pitch;
        unity::callv(*c, "set_sizeDelta", &mut [size.as_mut_ptr() as _])?;
        unity::callv(*c, "set_anchoredPosition", &mut [at.as_mut_ptr() as _])?;
    }
    let nav = component(list, "NavigatableMenu")?;
    symbols::call(nav, "ui.navigatable_menu.collect", &mut [])?;
    if !navigation_items(nav)?.contains(&widget) {
        symbols::call(nav, "ui.navigatable_menu.add_button", &mut [widget as *mut c_void])?;
    }

    log::info(format!("catalog: title list after: {}", list_layout(list)));
    // The window: the About window, larger, without its credits.
    // A window learns where its content sits when open as it wakes (on cloning) and parks it
    // off-screen. Clone About with its content at that open place, or ours would open off-screen.
    let about_content = find(about, "Content");
    let parked = unity::call_unbox::<[f32; 2]>(about_content, "get_anchoredPosition")?;
    let mut home = [0f32, 0.0];
    unity::callv(about_content, "set_anchoredPosition", &mut [home.as_mut_ptr() as _])?;
    let window = clone(about, menu, WINDOW);
    let mut parked_again = parked;
    unity::callv(about_content, "set_anchoredPosition", &mut [parked_again.as_mut_ptr() as _])?;
    // Its size stays: the window grows from one edge, and everything inside is placed around its centre.
    let window = window?;
    let credits = find(window, "Content/VList/Credits");
    if !credits.is_null() {
        destroy(credits)?;
    }
    log::info("catalog: added to the title screen");
    Ok(())
}

/// Height and pitch of `n` title buttons filling `top`..`bottom` with a 16-unit gap.
fn title_rows(top: f32, bottom: f32, n: usize) -> (f32, f32) {
    const GAP: f32 = 16.0;
    let n = n.max(1) as f32;
    let h = ((top - bottom) - (n - 1.0) * GAP) / n;
    (h, h + GAP)
}

/// The title list's layout settings and each button's height and place, for the log.
unsafe fn list_layout(list: *mut Object) -> String {
    let settings = component(list, "VerticalList").map(|v| format!("resizeChildren={:?} resizeSelf={:?} spacing={:?}",
        unity::field_val::<bool>(v, "resizeChildren").ok(), unity::field_val::<bool>(v, "resizeSelf").ok(), unity::field_val::<f32>(v, "spacing").ok())).unwrap_or_else(|e| e);
    let rect = unity::call_unbox::<[f32; 4]>(list, "get_rect").map(|r| format!("{}x{}", r[2], r[3])).unwrap_or_default();
    let rows: Vec<String> = children(list).into_iter().map(|c| {
        let r = unity::call_unbox::<[f32; 4]>(c, "get_rect").unwrap_or_default();
        let at = unity::call_unbox::<[f32; 2]>(c, "get_anchoredPosition").unwrap_or_default();
        format!("{} h{} y{}", unity::name(go_of(c).unwrap_or(null_mut())), r[3], at[1])
    }).collect();
    format!("{settings} size {rect}: {}", rows.join(", "))
}

/// The Catalog button: fill the window, then open it the way the main menu opens its others.
unsafe extern "C" fn on_open(_id: usize, _button: *mut Object, _m: *const c_void) {
    let _ = std::panic::catch_unwind(|| unsafe {
        if let Err(e) = open() {
            log::info(format!("catalog: couldn't open: {e}"));
        }
    });
}

unsafe fn open() -> R<()> {
    let ui = ui_root()?;
    let window = find(ui, &format!("MainMenu/{WINDOW}"));
    if window.is_null() {
        return Err("window not built".into());
    }
    PAGE.store(0, Ordering::Relaxed);
    fill(find(window, "Content"))?;
    // Open it exactly as the main menu opens About: point the main menu's About window field at
    // ours for the length of the About button's own click handler, then put it back.
    let main = component(find(ui, "MainMenu"), "MainMenu")?;
    let about = component(find(ui, "MainMenu/AboutMenu"), "SimpleWindow")?;
    let ours = component(window, "SimpleWindow")?;
    let about_button = component(find(ui, "MainMenu/Content/VList/AboutButton"), "Button")?;
    let a = crate::il2cpp::api();
    let mut slot = None;
    let mut k = (a.object_get_class)(main);
    'search: while !k.is_null() {
        let mut it = null_mut();
        loop {
            let f = (a.class_get_fields)(k, &mut it);
            if f.is_null() {
                break;
            }
            if (a.field_get_flags)(f) & 0x10 == 0 && crate::il2cpp::type_name((a.field_get_type)(f)) == "SimpleWindow" {
                let mut v: *mut Object = null_mut();
                (a.field_get_value)(main, f, &mut v as *mut _ as _);
                if v == about {
                    slot = Some(f);
                    break 'search;
                }
            }
        }
        k = (a.class_get_parent)(k);
    }
    let slot = slot.ok_or("the main menu's About window field wasn't found")?;
    // The main menu listens for About closing (that's what brings it back): give ours the same
    // listeners, the window's Action fields (opened, closing, closed).
    let mut copied = vec![];
    let mut k = (a.object_get_class)(about);
    while !k.is_null() {
        let mut it = null_mut();
        loop {
            let f = (a.class_get_fields)(k, &mut it);
            if f.is_null() {
                break;
            }
            if (a.field_get_flags)(f) & 0x10 == 0 && crate::il2cpp::type_name((a.field_get_type)(f)) == "System.Action" {
                let mut v: *mut Object = null_mut();
                (a.field_get_value)(about, f, &mut v as *mut _ as _);
                (a.field_set_value)(ours, f, v as *mut c_void);
                copied.push(!v.is_null());
            }
        }
        k = (a.class_get_parent)(k);
    }
    log::info(format!("catalog: opening; About's listeners copied (set: {copied:?})"));
    let click = crate::modmenu::field_by_type(about_button, "System.Action<Button>")?;
    let mut handler: *mut Object = null_mut();
    (a.field_get_value)(about_button, click, &mut handler as *mut _ as _);
    if handler.is_null() {
        return Err("the About button has no click handler".into());
    }
    let invoke = il2cpp::method((a.object_get_class)(handler), "Invoke", 1).ok_or("delegate Invoke missing")?;
    (a.field_set_value)(main, slot, ours as *mut c_void);
    let result = il2cpp::invoke(invoke, handler as *mut c_void, &mut [about_button as *mut c_void]);
    (a.field_set_value)(main, slot, about as *mut c_void);
    result.map(|_| ())
}

/// Runs `f` on the main thread a moment from now: a click must not destroy the button it came from.
fn later(f: fn()) {
    std::thread::spawn(move || {
        std::thread::sleep(std::time::Duration::from_millis(30));
        let _ = crate::pump::run(f);
    });
}

fn refill() {
    unsafe {
        let content = ui_root().map(|ui| find(ui, &format!("MainMenu/{WINDOW}/Content"))).unwrap_or(null_mut());
        if !content.is_null() {
            if let Err(e) = fill(content) {
                log::info(format!("catalog: {e}"));
            }
        }
    }
}

// ---- the window's contents -------------------------------------------------------------------------

/// Builds the window's contents afresh: title, status, tabs, cards and pages.
unsafe fn fill(content: *mut Object) -> R<()> {
    for c in children(content) {
        if unity::name(go_of(c)?).starts_with("Mod_") {
            destroy(c)?;
        }
    }
    let template = find(back_button(content)?, "LabelText");
    if template.is_null() {
        return Err("window layout changed".into());
    }
    *SHIFT.lock().unwrap() = offset(content).unwrap_or([0.0, 0.0]);
    // The window's own Back button (wired by the game) sits wherever its list lays it out, which is
    // under the cards: take it out of the list and put it below the grid.
    let back = back_button(content)?;
    let mut keep = false;
    unity::callv(back, "SetParent", &mut [content as *mut c_void, &mut keep as *mut bool as _])?;
    let snap = jibbers_distribution::manager::snapshot(&crate::game_dir());
    let status = status_text();
    *SHOWN.lock().unwrap() = (snap.catalog.as_ref().map(|c| c.sequence).unwrap_or(0), status.clone());

    let left = -(COLUMNS as f32 * (CARD[0] + GAP) - GAP) / 2.0;
    let wide = -2.0 * left;
    text(template, content, "Mod_Title", "Mod Catalog", [0.0, 488.0], [wide, 64.0], 54.0, 514)?;
    text(template, content, "Mod_Status", &status, [0.0, 422.0], [wide, 52.0], 17.0, 514)?;

    // Sort tabs on the left (the open one greyed, like the Options tabs), actions on the right.
    let row = 355.0;
    let sort = SORT.load(Ordering::Relaxed);
    let width = 180.0;
    text(template, content, "Mod_SortLabel", "Sort", [left + 45.0, row], [90.0, 60.0], 28.0, 514)?;
    for (i, name) in SORTS.iter().enumerate() {
        let b = button(content, &format!("Mod_Sort{i}"), name, on_sort as *const () as usize, i, [left + 100.0 + width / 2.0 + i as f32 * (width + 10.0), row], [width, 60.0])?;
        if i == sort {
            let mut no = false;
            symbols::call(b, "ui.behaviour.set_interactable", &mut [&mut no as *mut bool as _])?;
        }
    }
    let right = -left;
    let auto = if snap.config.auto_update { "Auto-update: On" } else { "Auto-update: Off" };
    button(content, "Mod_Auto", auto, on_auto as *const () as usize, 0, [right - 130.0, row], [260.0, 60.0])?;
    button(content, "Mod_Check", "Check for updates", on_check as *const () as usize, 0, [right - 260.0 - 10.0 - 140.0, row], [280.0, 60.0])?;

    // The cards of this page.
    let cards = match &snap.catalog {
        Some(catalog) => {
            let packages = catalog.selectable_packages()?;
            cards(&packages, &context(&packages, &snap), sort)
        }
        None => vec![],
    };
    let pages = cards.len().div_ceil(PER_PAGE).max(1);
    let page = PAGE.load(Ordering::Relaxed).min(pages - 1);
    PAGE.store(page, Ordering::Relaxed);
    let shown: Vec<(String, String)> = cards.into_iter().skip(page * PER_PAGE).take(PER_PAGE).collect();
    *CARDS.lock().unwrap() = shown.iter().map(|(id, _)| id.clone()).collect();
    if shown.is_empty() {
        let why = if snap.catalog.is_none() { "The mod catalog couldn't be loaded. Check for updates to try again." } else { "No mods in the catalog yet." };
        text(template, content, "Mod_Empty", why, [0.0, 0.0], [1200.0, 80.0], 30.0, 514)?;
    }
    for (i, (_, label)) in shown.iter().enumerate() {
        let (col, line) = ((i % COLUMNS) as f32, (i / COLUMNS) as f32);
        let x = left + CARD[0] / 2.0 + col * (CARD[0] + GAP);
        let y = GRID_TOP - CARD[1] / 2.0 - line * (CARD[1] + GAP);
        let b = button(content, &format!("Mod_Card{i}"), label, on_card as *const () as usize, i, [x, y], CARD)?;
        card_text(b)?;
    }
    let back = back_button(content)?;
    place(back, [0.0, GRID_TOP - 3.0 * (CARD[1] + GAP) - 92.0], [420.0, 72.0])?;
    style(component(find(back, "LabelText"), "TMP_Text")?, 30.0, 514, false)?;
    if pages > 1 {
        let y = GRID_TOP - 3.0 * (CARD[1] + GAP) - 16.0;
        button(content, "Mod_Prev", "Previous", on_page as *const () as usize, 0, [left + 100.0, y], [200.0, 48.0])?;
        text(template, content, "Mod_Page", &format!("Page {} of {pages}", page + 1), [0.0, y], [400.0, 48.0], 24.0, 514)?;
        button(content, "Mod_Next", "Next", on_page as *const () as usize, 1, [right - 100.0, y], [200.0, 48.0])?;
    }
    Ok(())
}

/// A game button, cloned from the window's own Back button (same look, sound and font), named,
/// labelled, pointed at `f` (called with `id`) and placed (centre `at`, `size`). Returns its Button.
unsafe fn button(parent: *mut Object, name: &str, label: &str, f: usize, id: usize, at: [f32; 2], size: [f32; 2]) -> R<*mut Object> {
    let tr = clone(back_button(parent)?, parent, name)?;
    set_text(tr, "LabelText", label);
    let b = component(tr, "Button")?;
    hook(b, "System.Action<Button>", f, id)?;
    place(tr, at, size)?;
    // The Back button's label is sized for a 150-high button: one line at a size that fits.
    style(component(find(tr, "LabelText"), "TMP_Text")?, (size[1] * 0.45).min(28.0), 514, false)?;
    Ok(b)
}

/// A text, cloned from the window's own label so it has the game's font.
unsafe fn text(template: *mut Object, parent: *mut Object, name: &str, s: &str, at: [f32; 2], size: [f32; 2], font: f32, align: i32) -> R<*mut Object> {
    let t = clone(template, parent, name)?;
    place(t, at, size)?;
    let tmp = component(t, "TMP_Text")?;
    style(tmp, font, align, true)?;
    unity::callv(tmp, "set_text", &mut [il2cpp::string(s) as *mut c_void])?;
    Ok(t)
}

/// The window's Back button: in its list until the first fill moves it into the content.
unsafe fn back_button(content: *mut Object) -> R<*mut Object> {
    [find(content, "BackButton"), find(content, "VList/BackButton")].into_iter().find(|b| !b.is_null()).ok_or_else(|| "window layout changed".to_string())
}

/// The window's distance from the screen's centre, in canvas units.
unsafe fn offset(content: *mut Object) -> R<[f32; 2]> {
    let window = unity::callv(content, "get_parent", &mut [])?;
    let ui = ui_root()?;
    let w = unity::call_unbox::<[f32; 3]>(window, "get_position")?;
    let c = unity::call_unbox::<[f32; 3]>(ui, "get_position")?;
    let scale = unity::call_unbox::<[f32; 3]>(ui, "get_lossyScale")?;
    if scale[0] <= 0.0 || scale[1] <= 0.0 {
        return Err("canvas has no scale".into());
    }
    Ok([(w[0] - c[0]) / scale[0], (w[1] - c[1]) / scale[1]])
}

/// Centre-anchors `tr` at `at` (from the screen's centre) with `size`.
unsafe fn place(tr: *mut Object, at: [f32; 2], size: [f32; 2]) -> R<()> {
    let shift = *SHIFT.lock().unwrap();
    let (mut mid, mut at, mut size) = ([0.5f32, 0.5], [at[0] - shift[0], at[1] - shift[1]], size);
    for m in ["set_anchorMin", "set_anchorMax", "set_pivot"] {
        unity::callv(tr, m, &mut [mid.as_mut_ptr() as _])?;
    }
    unity::callv(tr, "set_sizeDelta", &mut [size.as_mut_ptr() as _])?;
    unity::callv(tr, "set_anchoredPosition", &mut [at.as_mut_ptr() as _])?;
    let mut one = [1f32, 1.0, 1.0];
    unity::callv(tr, "set_localScale", &mut [one.as_mut_ptr() as _])?;
    Ok(())
}

/// Font size, alignment (TextAlignmentOptions) and wrapping of a TMP text.
unsafe fn style(tmp: *mut Object, font: f32, align: i32, wrap: bool) -> R<()> {
    let (mut font, mut align, mut off) = (font, align, false);
    let _ = unity::callv(tmp, "set_enableAutoSizing", &mut [&mut off as *mut bool as _]);
    unity::callv(tmp, "set_fontSize", &mut [&mut font as *mut f32 as _])?;
    unity::callv(tmp, "set_alignment", &mut [&mut align as *mut i32 as _])?;
    // TextMeshPro 3.2 replaced enableWordWrapping with textWrappingMode (1 = Normal).
    let mut mode = wrap as i32;
    let mut on = wrap;
    if unity::callv(tmp, "set_textWrappingMode", &mut [&mut mode as *mut i32 as _]).is_err() {
        let _ = unity::callv(tmp, "set_enableWordWrapping", &mut [&mut on as *mut bool as _]);
    }
    let mut rich = true;
    let _ = unity::callv(tmp, "set_richText", &mut [&mut rich as *mut bool as _]);
    Ok(())
}

/// A card's label: top-left, wrapped, plain style (the menus' small caps are for one-word labels), inset.
unsafe fn card_text(b: *mut Object) -> R<()> {
    let tr = unity::callv(b, "get_transform", &mut [])?;
    let tmp = component(find(tr, "LabelText"), "TMP_Text")?;
    style(tmp, 24.0, 257, true)?; // TopLeft
    let mut normal = 0i32;
    let _ = unity::callv(tmp, "set_fontStyle", &mut [&mut normal as *mut i32 as _]);
    let mut margin = [22f32, 14.0, 22.0, 12.0];
    let _ = unity::callv(tmp, "set_margin", &mut [margin.as_mut_ptr() as _]);
    Ok(())
}

// ---- clicks ------------------------------------------------------------------------------------------

unsafe extern "C" fn on_card(id: usize, _button: *mut Object, _m: *const c_void) {
    let _ = std::panic::catch_unwind(|| {
        let root = crate::game_dir();
        let result = (|| -> Result<(), String> {
            let key = CARDS.lock().unwrap().get(id).cloned().ok_or("catalog changed; reopen Catalog")?;
            let snap = jibbers_distribution::manager::snapshot(&root);
            let catalog = snap.catalog.as_ref().ok_or("public catalog is unavailable")?;
            let packages = catalog.selectable_packages()?;
            let package = packages.iter().find(|p| p.id == key).ok_or("catalog changed; reopen Catalog")?;
            let ctx = context(&packages, &snap);
            use jibbers_distribution::manager::{queue_updates, select, set_aside};
            match state(package, &ctx) {
                State::Unofficial => {
                    set_aside(&root, &ctx.unofficial[&key], true)?;
                    select(&root, &key, true)
                }
                State::Replacing => {
                    set_aside(&root, &ctx.unofficial[&key], false)?;
                    select(&root, &key, false)
                }
                State::UpdateAvailable => queue_updates(&root),
                State::Get | State::Removing => select(&root, &key, true),
                State::Installed | State::Updating | State::Installing => select(&root, &key, false),
            }
        })();
        if let Err(error) = result {
            jibbers_distribution::manager::set_message(&root, error);
        }
        later(refill);
    });
}

unsafe extern "C" fn on_sort(id: usize, _button: *mut Object, _m: *const c_void) {
    SORT.store(id.min(SORTS.len() - 1), Ordering::Relaxed);
    PAGE.store(0, Ordering::Relaxed);
    later(refill);
}

unsafe extern "C" fn on_page(id: usize, _button: *mut Object, _m: *const c_void) {
    let page = PAGE.load(Ordering::Relaxed);
    PAGE.store(if id == 0 { page.saturating_sub(1) } else { page + 1 }, Ordering::Relaxed);
    later(refill);
}

unsafe extern "C" fn on_auto(_id: usize, _button: *mut Object, _m: *const c_void) {
    let _ = std::panic::catch_unwind(|| {
        let root = crate::game_dir();
        let on = jibbers_distribution::manager::selected(&root, None);
        jibbers_distribution::manager::cancel_queued_updates(&root);
        if let Err(error) = jibbers_distribution::manager::set_auto_update(&root, !on) {
            jibbers_distribution::manager::set_message(&root, error);
        }
        later(refill);
    });
}

unsafe extern "C" fn on_check(_id: usize, _button: *mut Object, _m: *const c_void) {
    let _ = std::panic::catch_unwind(|| jibbers_distribution::manager::check(&crate::game_dir()));
}

fn status_text() -> String {
    crate::modmenu::store_text(jibbers_distribution::manager::message(&crate::game_dir()), &crate::core::unverified())
}

// ---- cards -------------------------------------------------------------------------------------------

/// What a card's state depends on besides the catalog.
pub(crate) struct Context<'a> {
    pub installed: &'a BTreeMap<String, InstalledPackage>,
    pub wanted: &'a [String],
    pub auto_update: bool,
    /// Unofficial copies of catalog mods that are loaded now: package id -> DLL path.
    pub unofficial: BTreeMap<String, String>,
    /// Unofficial DLLs queued to be set aside on the next launch.
    pub set_aside: Vec<String>,
}

/// A card's state, which is also what clicking it does.
#[derive(Clone, Copy, PartialEq, Debug)]
pub(crate) enum State {
    /// An unofficial copy is loaded: click to replace it with the catalog's.
    Unofficial,
    /// Replacing the unofficial copy on the next launch: click to keep it.
    Replacing,
    /// Auto-update is off: click to install the updates on the next launch.
    UpdateAvailable,
    /// Updating on the next launch: click to remove.
    Updating,
    Installed,
    /// Removed on the next launch: click to keep.
    Removing,
    /// Installed on the next launch: click to cancel.
    Installing,
    Get,
}

pub(crate) fn state(p: &Package, ctx: &Context) -> State {
    if let Some(path) = ctx.unofficial.get(&p.id) {
        return if ctx.set_aside.contains(path) { State::Replacing } else { State::Unofficial };
    }
    let picked = ctx.wanted.contains(&p.id);
    match (ctx.installed.get(&p.id), picked) {
        (Some(i), true) if i.version != p.version => if ctx.auto_update { State::Updating } else { State::UpdateAvailable },
        (Some(_), true) => State::Installed,
        (Some(_), false) => State::Removing,
        (None, true) => State::Installing,
        (None, false) => State::Get,
    }
}

/// The context for `packages` now: the loaded unofficial copies come from the loader's mod list.
fn context<'a>(packages: &[&Package], snap: &'a jibbers_distribution::manager::Snapshot) -> Context<'a> {
    let root = crate::game_dir();
    Context {
        installed: &snap.installed.packages,
        wanted: &snap.config.wanted,
        auto_update: snap.config.auto_update,
        unofficial: unofficial(packages, &crate::modmenu::unverified_plugins(), &crate::core::unverified()),
        set_aside: jibbers_distribution::manager::set_aside_list(&root),
    }
}

/// Matches loaded unofficial mods (mod id, name, plugin file) to catalog packages, by the package
/// id the catalog gives a mod (`jibbers_<mod id>`) or by name: package id -> the DLL's path.
pub(crate) fn unofficial(packages: &[&Package], mods: &[(String, String, String)], paths: &[String]) -> BTreeMap<String, String> {
    let norm = |s: &str| s.chars().filter(|c| c.is_ascii_alphanumeric()).collect::<String>().to_ascii_lowercase();
    let mut out = BTreeMap::new();
    for (id, name, file) in mods {
        // A C# file with several mods lists each as `<stem>.<mod id>`.
        let stem = file.split('.').next().unwrap_or(file);
        let Some(path) = paths.iter().find(|p| std::path::Path::new(p).file_stem().is_some_and(|s| s.eq_ignore_ascii_case(stem))) else { continue };
        if let Some(p) = packages.iter().find(|p| p.id == format!("jibbers_{id}") || norm(&p.name) == norm(name) || norm(&p.name) == norm(id)) {
            out.insert(p.id.clone(), path.clone());
        }
    }
    out
}

/// The catalog as cards in the order of sort `sort` (an index into SORTS): (package id, label). The
/// label is rich text for a light card: name and version, a short description, then state and size.
pub(crate) fn cards(packages: &[&Package], ctx: &Context, sort: usize) -> Vec<(String, String)> {
    // 0 needs attention (update, unofficial), 1 installed, 2 picked, 3 the rest
    let rank = |p: &Package| match state(p, ctx) {
        State::Unofficial | State::Replacing | State::UpdateAvailable | State::Updating => 0,
        State::Installed | State::Removing => 1,
        State::Installing => 2,
        State::Get => 3,
    };
    let size = |p: &Package| p.files.iter().map(|f| f.size).sum::<u64>();
    let mut list: Vec<&Package> = packages.to_vec();
    list.sort_by(|a, b| {
        let by_name = a.name.to_lowercase().cmp(&b.name.to_lowercase());
        match sort {
            1 => (rank(a) > 1).cmp(&(rank(b) > 1)).then(by_name),
            2 => rank(a).cmp(&rank(b)).then(by_name),
            3 => size(a).cmp(&size(b)).then(by_name),
            _ => by_name,
        }
    });
    list.into_iter()
        .map(|p| {
            let state = match state(p, ctx) {
                State::Unofficial => "<color=#8A5A00>Unofficial copy installed</color></b>  -  click to replace<b>",
                State::Replacing => "<color=#1D4F86>Replaced with official on next launch</color>",
                State::UpdateAvailable => "<color=#8A5A00>Update available</color></b>  -  click to update<b>",
                State::Updating => "<color=#1D4F86>Updates on next launch</color>",
                State::Installed => "<color=#1E6B34>Installed</color>",
                State::Removing => "<color=#9B2C2C>Removed on next launch</color>",
                State::Installing => "<color=#1D4F86>Installs on next launch</color>",
                State::Get => "<color=#3D4B52>Get</color>",
            };
            let label = format!(
                "<size=125%><b>{}</b></size>  <color=#4A5A62>v{}</color>\n<size=90%><color=#2A363C>{}</color></size>\n<size=88%><b>{state}</b>   <color=#4A5A62>{}</color></size>",
                p.name, p.version, short(&p.description, 100), size_text(size(p))
            );
            (p.id.clone(), label)
        })
        .collect()
}

/// `text` cut at a word to about `max` characters.
pub(crate) fn short(text: &str, max: usize) -> String {
    let text = text.trim();
    if text.chars().count() <= max {
        return text.to_string();
    }
    let cut: String = text.chars().take(max).collect();
    let cut = cut.rfind(' ').map(|i| &cut[..i]).unwrap_or(&cut);
    format!("{}...", cut.trim_end_matches([',', '.', ':', ';', ' ']))
}

pub(crate) fn size_text(bytes: u64) -> String {
    match bytes {
        b if b >= 1 << 20 => format!("{:.1} MB", b as f64 / (1 << 20) as f64),
        b if b >= 1 << 10 => format!("{} KB", b >> 10),
        b => format!("{b} B"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn package(id: &str, name: &str, version: &str, size: u64) -> Package {
        serde_json::from_value(json!({"id": id, "name": name, "description": "A mod that does a thing, and then another thing, and keeps going for quite a while past the cut, really.", "version": version,
            "files": [{"path": format!("JibbersMods/cs/{id}.dll"), "url": "https://example.invalid/x", "size": size, "sha256": "00"}]})).unwrap()
    }

    fn ctx<'a>(installed: &'a BTreeMap<String, InstalledPackage>, wanted: &'a [String], auto_update: bool) -> Context<'a> {
        Context { installed, wanted, auto_update, unofficial: BTreeMap::new(), set_aside: vec![] }
    }

    #[test]
    fn cards_sort_and_show_their_state() {
        let (a, b, c, d) = (package("a", "Zebra", "1.0.0", 5 << 20), package("b", "apple", "2.0.0", 900), package("c", "Mango", "1.0.0", 3000), package("d", "Kiwi", "1.0.0", 10));
        let packages = vec![&a, &b, &c, &d];
        let installed: BTreeMap<_, _> = [
            ("a".to_string(), InstalledPackage { version: "1.0.0".into(), files: vec![] }),
            ("b".to_string(), InstalledPackage { version: "1.0.0".into(), files: vec![] }),
        ].into();
        let wanted = vec!["a".to_string(), "b".to_string(), "c".to_string()];
        let manual = ctx(&installed, &wanted, false);
        let order = |sort| cards(&packages, &manual, sort).into_iter().map(|(id, _)| id).collect::<Vec<_>>();
        assert_eq!(order(0), ["b", "d", "c", "a"], "by name, ignoring case");
        assert_eq!(order(1), ["b", "a", "d", "c"], "installed first");
        assert_eq!(order(2), ["b", "a", "c", "d"], "update, installed, picked, the rest");
        assert_eq!(order(3), ["d", "b", "c", "a"], "smallest first");
        let all = cards(&packages, &manual, 0);
        let label = |id: &str| all.iter().find(|(i, _)| i == id).unwrap().1.clone();
        assert!(label("b").contains("Update available") && label("b").contains("click to update") && label("b").contains("<b>apple</b>"), "{}", label("b"));
        assert!(label("a").contains("Installed") && label("a").contains("5.0 MB"), "{}", label("a"));
        assert!(label("c").contains("Installs on next launch") && label("c").contains("2 KB"));
        assert!(label("d").contains(">Get<") && label("d").contains("10 B"));
        assert!(!label("d").contains("really") && label("d").contains("..."), "long descriptions are cut");
        // With auto-update on, an update is simply on its way.
        assert_eq!(state(&b, &ctx(&installed, &wanted, true)), State::Updating);
    }

    #[test]
    fn unofficial_copies_are_matched_and_replaced() {
        let (rumble, alpine, other) = (package("jibbers_rumble", "Rumble", "1.0.0", 1), package("jibbers_atmosphere", "Alpine Vision", "1.0.0", 1), package("jibbers_x", "X", "1.0.0", 1));
        let packages = vec![&rumble, &alpine, &other];
        let mods = vec![
            ("rumble".to_string(), "Rumble".to_string(), "Rumble".to_string()),
            ("alpinevision".to_string(), "Alpine Vision".to_string(), "AlpineVision".to_string()),
            ("firstperson".to_string(), "First Person".to_string(), "FirstPerson".to_string()),
        ];
        let paths = vec!["JibbersMods/cs/Rumble/Rumble.dll".to_string(), "JibbersMods/cs/AlpineVision/AlpineVision.dll".to_string(), "JibbersMods/cs/FirstPerson/FirstPerson.dll".to_string()];
        let found = unofficial(&packages, &mods, &paths);
        assert_eq!(found.get("jibbers_rumble").map(String::as_str), Some("JibbersMods/cs/Rumble/Rumble.dll"), "by package id");
        assert_eq!(found.get("jibbers_atmosphere").map(String::as_str), Some("JibbersMods/cs/AlpineVision/AlpineVision.dll"), "by name");
        assert_eq!(found.len(), 2, "First Person isn't in the catalog");
        let (installed, wanted) = (BTreeMap::new(), vec![]);
        let mut c = ctx(&installed, &wanted, true);
        c.unofficial = found;
        assert_eq!(state(&rumble, &c), State::Unofficial);
        assert!(cards(&packages, &c, 0).iter().any(|(id, l)| id == "jibbers_rumble" && l.contains("Unofficial copy installed")));
        c.set_aside.push("JibbersMods/cs/Rumble/Rumble.dll".into());
        assert_eq!(state(&rumble, &c), State::Replacing);
        assert_eq!(state(&other, &c), State::Get);
    }

    #[test]
    fn eight_title_buttons_fill_the_span_seven_used() {
        // Seven 120-high buttons 140 apart, from 350 down to -490: 410 to -550.
        let (h, pitch) = title_rows(410.0, -550.0, 8);
        assert_eq!((h, pitch), (106.0, 122.0));
        assert_eq!(410.0 - h / 2.0 - 7.0 * pitch - h / 2.0, -550.0);
    }

    #[test]
    fn descriptions_are_cut_at_a_word() {
        assert_eq!(short("Short one.", 96), "Short one.");
        assert_eq!(short("one two three four", 9), "one two...");
        assert_eq!(short("one, two three", 5), "one...");
    }
}
