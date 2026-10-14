"""Конструктор шаблонов курсовых и ВКР — оконное приложение для Windows."""
import copy
import ctypes
import json
import os
import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, font as tkfont, messagebox, ttk

import make_template
from make_template import generate, load_config

APP_TITLE = "Конструктор шаблонов работ"
BASE = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent))
SETTINGS = Path(os.environ.get("APPDATA", Path.home())) / "KonstruktorShablonov" / "last.json"

BUILTIN = {
    "Курсовая работа": BASE / "config" / "kursovaya.json",
    "Выпускная квалификационная работа (ВКР)": BASE / "config" / "vkr.json",
}

LABELS = {
    "ministry": "Министерство",
    "org_form": "Тип учреждения",
    "university": "Университет",
    "institute": "Институт / факультет",
    "department": "Кафедра",
    "work_type": "Вид работы",
    "work_kind": "Уточнение вида работы",
    "discipline": "Дисциплина",
    "topic": "Тема работы",
    "specialty": "Направление подготовки",
    "profile": "Профиль",
    "course": "Курс",
    "group": "Группа",
    "student": "Студент (И. О. Фамилия)",
    "supervisor_position": "Должность руководителя",
    "supervisor": "Руководитель (И. О. Фамилия)",
    "head_position": "Должность зав. кафедрой",
    "head": "Зав. кафедрой (И. О. Фамилия)",
    "normcontrol": "Нормоконтролёр (И. О. Фамилия)",
    "city": "Город",
    "year": "Год",
}
# Эти поля относятся к виду работы, а не к студенту — их не запоминаем между запусками
PROFILE_FIELDS = {"work_type", "work_kind", "ministry", "org_form"}

LEVELS = "*levels"  # заголовки 1–3 уровня настраиваются одним переключателем
FONTS = ["Times New Roman", "Arial", "Calibri", "PT Serif", "PT Astra Serif"]
FORMAT = [
    ("Страница", [
        ("Левое поле, мм", ("page", "margins_mm", "left"), "spin", (0, 50, 1)),
        ("Правое поле, мм", ("page", "margins_mm", "right"), "spin", (0, 50, 1)),
        ("Верхнее поле, мм", ("page", "margins_mm", "top"), "spin", (0, 50, 1)),
        ("Нижнее поле, мм", ("page", "margins_mm", "bottom"), "spin", (0, 50, 1)),
        ("Номер страницы", ("page", "numbering", "align"), "choice", {"center": "внизу по центру", "right": "внизу справа"}),
    ]),
    ("Основной текст", [
        ("Шрифт", ("text", "font"), "combo", FONTS),
        ("Размер шрифта, пт", ("text", "size_pt"), "spin", (10, 16, 1)),
        ("Межстрочный интервал", ("text", "line_spacing"), "combo", ["1.0", "1.15", "1.5", "2.0"]),
        ("Абзацный отступ, см", ("text", "first_line_indent_cm"), "spin", (0, 2.5, 0.05)),
        ("Выравнивание", ("text", "align"), "choice", {"justify": "по ширине", "left": "по левому краю"}),
        ("Автоматические переносы", ("text", "hyphenation"), "check", None),
    ]),
    ("Заголовки", [
        ("Заголовки глав и параграфов", ("headings", LEVELS, "align"), "choice",
         {"indent": "с абзацного отступа", "center": "по центру"}),
        ("Каждая глава с новой страницы", ("headings", "level1", "new_page"), "check", None),
        ("ВВЕДЕНИЕ, ЗАКЛЮЧЕНИЕ прописными", ("headings", "structural", "case"), "checkval", ("upper", "as_is")),
        ("Уровней в содержании", ("headings", "toc_levels"), "spin", (1, 3, 1)),
    ]),
    ("Рисунки и таблицы", [
        ("Подпись рисунка", ("captions", "figure", "prefix"), "combo", ["Рисунок", "Рис."]),
        ("Вид подписи", ("captions", "separator"), "choice", {" – ": "Рисунок 1 – Название", ". ": "Рисунок 1. Название"}),
        ("Подпись таблицы", ("captions", "table", "align"), "choice", {"left": "слева", "center": "по центру"}),
        ("Шрифт в таблицах, пт", ("captions", "table_text_size_pt"), "spin", (10, 14, 1)),
    ]),
]


def get_path(cfg, path):
    *parents, key = path
    node = cfg
    for p in parents:
        node = node["level1"] if p == LEVELS else node[p]
    return node[key]


def set_path(cfg, path, value):
    *parents, key = path
    targets = [cfg]
    for p in parents:
        targets = [t[f"level{i}"] for t in targets for i in (1, 2, 3)] if p == LEVELS else [t[p] for t in targets]
    for t in targets:
        t[key] = value


def is_placeholder(value):
    v = (value or "").strip()
    return not v or (v.startswith("[") and v.endswith("]")) or v == "И. О. Фамилия"


def load_settings():
    try:
        return json.loads(SETTINGS.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def save_settings(data):
    try:
        SETTINGS.parent.mkdir(parents=True, exist_ok=True)
        SETTINGS.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    except OSError:
        pass


class HintEntry(ttk.Entry):
    """Поле ввода с серой подсказкой, пока оно пустое."""

    def __init__(self, master, hint="", **kw):
        self.var = tk.StringVar()
        super().__init__(master, textvariable=self.var, **kw)
        self.hint, self.showing = hint, False
        self.bind("<FocusIn>", self._hide_hint)
        self.bind("<FocusOut>", self._show_hint)
        self._show_hint()

    def _show_hint(self, _=None):
        if not self.var.get() and self.hint:
            self.showing = True
            self.configure(foreground="#8a8a8a")
            self.var.set(self.hint)

    def _hide_hint(self, _=None):
        if self.showing:
            self.showing = False
            self.var.set("")
            self.configure(foreground="")

    def get_value(self):
        return "" if self.showing else self.var.get().strip()

    def set_value(self, value):
        self._hide_hint()
        self.var.set(value)
        if self.focus_get() is not self:
            self._show_hint()


class App:
    def __init__(self, root):
        self.root = root
        root.title(APP_TITLE)
        root.geometry("900x720")
        root.minsize(780, 560)
        self.settings = load_settings()
        self.profiles = {name: None for name in BUILTIN}  # имя → загруженный конфиг (лениво)
        self.paths = dict(BUILTIN)
        self.current = None
        self.cfg = None
        self.field_entries = {}
        self.format_vars = []

        self._build_styles()
        self._build_top()
        self.notebook = ttk.Notebook(root)
        self.notebook.pack(fill="both", expand=True, padx=12, pady=(0, 8))
        self._build_title_tab()
        self._build_format_tab()
        self._build_bottom()

        self.profile_box.current(0)
        self.switch_profile()

    # ------------------------------------------------------------ интерфейс

    def _build_styles(self):
        style = ttk.Style()
        if "vista" in style.theme_names():
            style.theme_use("vista")
        tkfont.nametofont("TkDefaultFont").configure(family="Segoe UI", size=10)
        tkfont.nametofont("TkTextFont").configure(family="Segoe UI", size=10)
        style.configure("Header.TLabel", font=("Segoe UI Semibold", 15))
        style.configure("Muted.TLabel", foreground="#666666")
        style.configure("Accent.TButton", font=("Segoe UI Semibold", 11), padding=(18, 8))
        style.configure("TLabelframe.Label", font=("Segoe UI Semibold", 10))

    def _build_top(self):
        top = ttk.Frame(self.root, padding=(12, 12, 12, 8))
        top.pack(fill="x")
        ttk.Label(top, text=APP_TITLE, style="Header.TLabel").pack(anchor="w")
        ttk.Label(top, text="Заполните титульный лист, при необходимости поправьте правила оформления "
                            "под свою методичку и нажмите «Создать шаблон».", style="Muted.TLabel").pack(anchor="w")
        row = ttk.Frame(top)
        row.pack(fill="x", pady=(10, 0))
        ttk.Label(row, text="Вид работы:").pack(side="left")
        self.profile_box = ttk.Combobox(row, state="readonly", width=44, values=list(self.profiles))
        self.profile_box.pack(side="left", padx=8)
        self.profile_box.bind("<<ComboboxSelected>>", lambda e: self.switch_profile())
        ttk.Button(row, text="Сохранить профиль…", command=self.save_profile).pack(side="right")
        ttk.Button(row, text="Открыть профиль…", command=self.open_profile).pack(side="right", padx=6)

    def _build_title_tab(self):
        outer = ttk.Frame(self.notebook)
        self.notebook.add(outer, text="  Титульный лист  ")
        canvas = tk.Canvas(outer, highlightthickness=0, borderwidth=0)
        bar = ttk.Scrollbar(outer, orient="vertical", command=canvas.yview)
        self.title_frame = ttk.Frame(canvas, padding=16)
        self.title_frame.bind("<Configure>", lambda e: canvas.configure(scrollregion=canvas.bbox("all")))
        win = canvas.create_window((0, 0), window=self.title_frame, anchor="nw")
        canvas.bind("<Configure>", lambda e: canvas.itemconfigure(win, width=e.width))
        canvas.configure(yscrollcommand=bar.set)
        canvas.pack(side="left", fill="both", expand=True)
        bar.pack(side="right", fill="y")
        scroll = lambda e: canvas.yview_scroll(int(-e.delta / 120), "units")
        canvas.bind("<Enter>", lambda e: canvas.bind_all("<MouseWheel>", scroll))
        canvas.bind("<Leave>", lambda e: canvas.unbind_all("<MouseWheel>"))
        self.title_canvas = canvas

    def _fill_title_tab(self):
        for w in self.title_frame.winfo_children():
            w.destroy()
        self.field_entries = {}
        self.title_frame.columnconfigure(1, weight=1)
        ttk.Label(self.title_frame, text="Пустые поля останутся в шаблоне подсказками в [скобках] — "
                                         "их можно заполнить позже прямо в Word.",
                  style="Muted.TLabel").grid(row=0, column=0, columnspan=2, sticky="w", pady=(0, 10))
        remembered = self.settings.get("fields", {})
        row = 1
        for key, value in self.cfg["fields"].items():
            if key.startswith("_"):
                continue
            ttk.Label(self.title_frame, text=LABELS.get(key, key) + ":").grid(
                row=row, column=0, sticky="w", padx=(0, 12), pady=4)
            hint = "текущий год" if key == "year" else value
            entry = HintEntry(self.title_frame, hint=hint if is_placeholder(value) else "")
            entry.grid(row=row, column=1, sticky="ew", pady=4)
            if key not in PROFILE_FIELDS and remembered.get(key):
                entry.set_value(remembered[key])
            elif not is_placeholder(value):
                entry.set_value(value)
            self.field_entries[key] = entry
            row += 1
        self.title_canvas.yview_moveto(0)

    def _build_format_tab(self):
        frame = ttk.Frame(self.notebook, padding=16)
        self.notebook.add(frame, text="  Правила оформления  ")
        for col in (0, 1):
            frame.columnconfigure(col, weight=1, uniform="g")
        for idx, (title, controls) in enumerate(FORMAT):
            box = ttk.LabelFrame(frame, text=title, padding=(12, 8))
            box.grid(row=idx // 2, column=idx % 2, sticky="nsew", padx=6, pady=6)
            box.columnconfigure(1, weight=1)
            for r, (label, path, kind, opts) in enumerate(controls):
                var = self._make_control(box, r, label, kind, opts)
                self.format_vars.append((path, kind, opts, var))
        ttk.Label(frame, text="По умолчанию — требования ГОСТ 7.32-2017. Изменения можно сохранить "
                              "в профиль кнопкой «Сохранить профиль…».",
                  style="Muted.TLabel").grid(row=2, column=0, columnspan=2, sticky="w", padx=6, pady=(8, 0))

    def _make_control(self, box, r, label, kind, opts):
        if kind in ("check", "checkval"):
            var = tk.BooleanVar()
            ttk.Checkbutton(box, text=label, variable=var).grid(row=r, column=0, columnspan=2, sticky="w", pady=4)
            return var
        ttk.Label(box, text=label).grid(row=r, column=0, sticky="w", padx=(0, 10), pady=4)
        var = tk.StringVar()
        if kind == "spin":
            lo, hi, step = opts
            w = ttk.Spinbox(box, from_=lo, to=hi, increment=step, textvariable=var, width=8)
        elif kind == "choice":
            w = ttk.Combobox(box, state="readonly", values=list(opts.values()), textvariable=var, width=24)
        else:
            w = ttk.Combobox(box, values=opts, textvariable=var, width=24)
        w.grid(row=r, column=1, sticky="w", pady=4)
        return var

    def _build_bottom(self):
        bottom = ttk.Frame(self.root, padding=(12, 0, 12, 12))
        bottom.pack(fill="x")
        self.pdf_var = tk.BooleanVar(value=self.settings.get("pdf", False))
        self.open_var = tk.BooleanVar(value=self.settings.get("open", True))
        opts = ttk.Frame(bottom)
        opts.pack(side="left")
        ttk.Checkbutton(opts, text="Сохранить также PDF", variable=self.pdf_var).pack(anchor="w")
        ttk.Checkbutton(opts, text="Открыть шаблон после создания", variable=self.open_var).pack(anchor="w")
        self.make_btn = ttk.Button(bottom, text="Создать шаблон", style="Accent.TButton", command=self.make)
        self.make_btn.pack(side="right")
        self.progress = ttk.Progressbar(bottom, mode="indeterminate", length=120)
        self.status = ttk.Label(self.root, text="", style="Muted.TLabel", padding=(12, 0, 12, 10))
        self.status.pack(fill="x")

    # ------------------------------------------------------------ данные

    def switch_profile(self):
        if self.current:
            self.apply_format(self.cfg)
            self.remember_fields()
        name = self.profile_box.get()
        if self.profiles[name] is None:
            self.profiles[name] = load_config(self.paths[name])
        self.current, self.cfg = name, self.profiles[name]
        self._fill_title_tab()
        self.load_format(self.cfg)

    def load_format(self, cfg):
        for path, kind, opts, var in self.format_vars:
            value = get_path(cfg, path)
            if kind == "check":
                var.set(bool(value))
            elif kind == "checkval":
                var.set(value == opts[0])
            elif kind == "choice":
                var.set(opts.get(value, value))
            else:
                var.set(str(value))

    def apply_format(self, cfg):
        for path, kind, opts, var in self.format_vars:
            old = get_path(cfg, path)
            if kind == "check":
                value = var.get()
            elif kind == "checkval":
                value = opts[0] if var.get() else opts[1]
            elif kind == "choice":
                value = next((k for k, v in opts.items() if v == var.get()), old)
            elif isinstance(old, (int, float)) and not isinstance(old, bool):
                try:
                    value = float(var.get().replace(",", "."))
                    value = int(value) if isinstance(old, int) and value.is_integer() else value
                except ValueError:
                    raise ValueError(f"Некорректное число в поле «{self._label_of(path)}»: {var.get()}")
            else:
                value = var.get().strip() or old
            if path == ("text", "size_pt") and value != old:
                # заголовки и подписи того же кегля, что и текст, меняются вместе с ним
                for node in [cfg["headings"][k] for k in ("structural", "level1", "level2", "level3")] + \
                            [cfg["captions"]["figure"], cfg["captions"]["table"]]:
                    if node.get("size_pt") == old:
                        node["size_pt"] = value
            set_path(cfg, path, value)

    def _label_of(self, path):
        return next(label for _, controls in FORMAT for label, p, *_ in controls if p == path)

    def collect(self):
        """Конфиг для сборки: правила с вкладки «Оформление» + заполненные поля титульного листа."""
        cfg = copy.deepcopy(self.cfg)
        self.apply_format(cfg)
        for key, entry in self.field_entries.items():
            if entry.get_value():
                cfg["fields"][key] = entry.get_value()
            elif key == "year":
                cfg["fields"][key] = ""
        return cfg

    def remember_fields(self):
        fields = self.settings.setdefault("fields", {})
        for key, entry in self.field_entries.items():
            if key not in PROFILE_FIELDS:
                fields[key] = entry.get_value()
        self.settings.update(pdf=self.pdf_var.get(), open=self.open_var.get())
        save_settings(self.settings)

    def open_profile(self):
        path = filedialog.askopenfilename(title="Открыть профиль оформления",
                                          filetypes=[("Профиль оформления", "*.json")])
        if not path:
            return
        try:
            cfg = load_config(Path(path))
            cfg["structure"], cfg["fields"], cfg["title_page"]  # проверяем, что это профиль
        except Exception as e:  # noqa: BLE001 — показываем пользователю любую ошибку чтения
            messagebox.showerror(APP_TITLE, f"Не удалось открыть профиль:\n{e}")
            return
        name = f"{cfg.get('name', 'Профиль')} ({Path(path).name})"
        self.profiles[name], self.paths[name] = cfg, Path(path)
        self.profile_box.configure(values=list(self.profiles))
        self.profile_box.set(name)
        self.switch_profile()

    def save_profile(self):
        try:
            cfg = self.collect()
        except ValueError as e:
            messagebox.showerror(APP_TITLE, str(e))
            return
        path = filedialog.asksaveasfilename(title="Сохранить профиль оформления", defaultextension=".json",
                                            initialfile=f"{cfg.get('name', 'Профиль')}.json",
                                            filetypes=[("Профиль оформления", "*.json")])
        if path:
            cfg.pop("extends", None)
            Path(path).write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
            self.status.configure(text=f"Профиль сохранён: {path}")

    # ------------------------------------------------------------ сборка

    def make(self):
        try:
            cfg = self.collect()
        except ValueError as e:
            messagebox.showerror(APP_TITLE, str(e))
            return
        self.remember_fields()
        student = self.field_entries.get("student")
        suffix = f" – {student.get_value()}" if student and student.get_value() else ""
        path = filedialog.asksaveasfilename(
            title="Куда сохранить шаблон", defaultextension=".docx",
            initialdir=self.settings.get("last_dir") or str(Path.home() / "Documents"),
            initialfile=f"{cfg['name']}{suffix}.docx", filetypes=[("Документ Word", "*.docx")])
        if not path:
            return
        self.settings["last_dir"] = str(Path(path).parent)
        save_settings(self.settings)
        self.make_btn.configure(state="disabled")
        self.progress.pack(side="right", padx=12)
        self.progress.start(12)
        self.status.configure(text="Собираю шаблон… Word обновляет содержание, это может занять до минуты.")
        threading.Thread(target=self._worker, args=(cfg, Path(path), self.pdf_var.get()), daemon=True).start()

    def _worker(self, cfg, path, pdf):
        try:
            result = generate(cfg, path, pdf=pdf)
            self.root.after(0, self._done, result, None)
        except PermissionError:
            self.root.after(0, self._done, None, "Файл открыт в другой программе. Закройте его в Word и повторите.")
        except Exception as e:  # noqa: BLE001
            self.root.after(0, self._done, None, f"Не удалось создать шаблон:\n{e}")

    def _done(self, result, error):
        self.progress.stop()
        self.progress.pack_forget()
        self.make_btn.configure(state="normal")
        if error:
            self.status.configure(text="")
            messagebox.showerror(APP_TITLE, error)
            return
        docx, pdf, word = result
        note = "" if word else " Содержание обновится при первом открытии в Word."
        self.status.configure(text=f"Готово: {docx}" + (f" и PDF" if pdf else "") + "." + note)
        if self.open_var.get():
            os.startfile(docx)

    def on_close(self):
        self.remember_fields()
        self.root.destroy()


def main():
    if len(sys.argv) > 1:  # режим командной строки: ShablonRabot.exe профиль.json -o файл.docx [--pdf]
        make_template.main()
        return
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(1)  # чёткий текст на мониторах с масштабированием
    except (AttributeError, OSError):
        pass
    root = tk.Tk()
    icon = BASE / "icon.png"
    if icon.exists():
        root.iconphoto(True, tk.PhotoImage(file=str(icon)))
    app = App(root)
    root.protocol("WM_DELETE_WINDOW", app.on_close)
    root.mainloop()


if __name__ == "__main__":
    main()
