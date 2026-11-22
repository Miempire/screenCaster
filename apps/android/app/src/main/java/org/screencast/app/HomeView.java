package org.screencast.app;

import android.annotation.SuppressLint;
import android.app.AlertDialog;
import android.content.Context;
import android.graphics.Typeface;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.List;
import java.util.Locale;

/**
 * The launcher: a remote-assistance card with the nine-digit code, the saved device list, and a
 * two-tab bottom bar. Visual language follows the dark + green remote-desktop apps the user is
 * used to; every control here is wired to the real session, there are no dead placeholders.
 */
@SuppressLint("ViewConstructor")
final class HomeView extends FrameLayout {

    interface Listener {
        void onConnect(DeviceBook.Device device);

        void onDelete(DeviceBook.Device device);
    }

    private static final int TAB_DEVICES = 0;
    private static final int TAB_ABOUT = 1;

    private final Listener listener;
    private final DeviceBook book;
    private final Handler handler = new Handler(Looper.getMainLooper());

    private final ScrollView devicesPage;
    private final ScrollView aboutPage;
    private final LinearLayout devicesList;
    private final TextView devicesCount;
    private final TextView noticeView;
    private final TextView directToggle;
    private final LinearLayout advancedBox;
    private final CodeFieldView codeField;
    private final EditText hostField;
    private final EditText videoPortField;
    private final EditText controlPortField;
    private final ImageView[] tabIcons = new ImageView[2];
    private final TextView[] tabLabels = new TextView[2];

    private boolean advancedOpen;
    private int currentTab;
    private final Runnable hideNotice;

    HomeView(Context context, Listener listener, String versionText) {
        super(context);
        this.listener = listener;
        this.book = new DeviceBook(context);
        setBackgroundColor(UiKit.BG);

        devicesList = new LinearLayout(context);
        devicesList.setOrientation(LinearLayout.VERTICAL);

        codeField = new CodeFieldView(context);
        directToggle = UiKit.text(context, "", 12, UiKit.TEXT_DIM, true);
        directToggle.setGravity(Gravity.CENTER);
        directToggle.setPadding(0, UiKit.dp(context, 12), 0, UiKit.dp(context, 12));
        directToggle.setOnClickListener(view -> toggleAdvanced());

        hostField = field(context, R.string.host_hint, InputType.TYPE_CLASS_TEXT);
        videoPortField = field(context, R.string.video_port_hint, InputType.TYPE_CLASS_NUMBER);
        controlPortField = field(context, R.string.control_port_hint, InputType.TYPE_CLASS_NUMBER);
        videoPortField.setText(R.string.default_video_port);
        controlPortField.setText(R.string.default_control_port);

        advancedBox = buildAdvanced(context);
        devicesCount = UiKit.text(context, "", 11, UiKit.TEXT_DIM, false);

        devicesPage = new ScrollView(context);
        devicesPage.setFillViewport(true);
        devicesPage.addView(buildDevicesPage(context), new ScrollView.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        aboutPage = new ScrollView(context);
        aboutPage.setFillViewport(true);
        aboutPage.addView(buildAboutPage(context, versionText), new ScrollView.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        aboutPage.setVisibility(View.GONE);

        noticeView = UiKit.text(context, "", 12, UiKit.TEXT, false);
        noticeView.setGravity(Gravity.CENTER);
        noticeView.setBackground(UiKit.rounded(context, UiKit.SURFACE_HIGH, 12));
        noticeView.setPadding(UiKit.dp(context, 14), UiKit.dp(context, 9), UiKit.dp(context, 14),
                UiKit.dp(context, 9));
        noticeView.setVisibility(View.GONE);

        FrameLayout.LayoutParams pageParams = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT);
        pageParams.bottomMargin = UiKit.dp(context, 72);
        addView(devicesPage, pageParams);
        FrameLayout.LayoutParams aboutParams = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT);
        aboutParams.bottomMargin = UiKit.dp(context, 72);
        addView(aboutPage, aboutParams);

        addView(buildNav(context), navParams(context));

        FrameLayout.LayoutParams noticeParams = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        noticeParams.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
        noticeParams.bottomMargin = UiKit.dp(context, 92);
        addView(noticeView, noticeParams);

        refreshDevices();
        setTab(TAB_DEVICES);
        updateDirectToggle();
        hideNotice = () -> noticeView.setVisibility(View.GONE);
    }

    /* ------------------------------------------------------------------ */
    /* Page construction                                                   */
    /* ------------------------------------------------------------------ */

    private View buildDevicesPage(Context context) {
        LinearLayout page = new LinearLayout(context);
        page.setOrientation(LinearLayout.VERTICAL);
        int padding = UiKit.dp(context, 20);
        page.setPadding(padding, UiKit.dp(context, 26), padding, UiKit.dp(context, 24));

        page.addView(buildHeader(context));

        LinearLayout hero = new LinearLayout(context);
        hero.setOrientation(LinearLayout.VERTICAL);
        hero.setPadding(UiKit.dp(context, 18), UiKit.dp(context, 18), UiKit.dp(context, 18),
                UiKit.dp(context, 16));
        hero.setBackground(UiKit.rounded(context, UiKit.SURFACE, 20));
        LinearLayout.LayoutParams heroParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        heroParams.topMargin = UiKit.dp(context, 24);
        page.addView(hero, heroParams);

        TextView title = UiKit.text(context, context.getString(R.string.remote_assist), 17,
                UiKit.TEXT, true);
        hero.addView(title);
        TextView hint = UiKit.text(context, context.getString(R.string.remote_assist_hint), 11,
                UiKit.TEXT_DIM, false);
        LinearLayout.LayoutParams hintParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        hintParams.topMargin = UiKit.dp(context, 5);
        hero.addView(hint, hintParams);

        LinearLayout.LayoutParams codeParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        codeParams.topMargin = UiKit.dp(context, 16);
        hero.addView(codeField, codeParams);

        TextView connectButton = UiKit.button(context, context.getString(R.string.connect), true);
        connectButton.setOnClickListener(view -> submit());
        LinearLayout.LayoutParams connectParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        connectParams.topMargin = UiKit.dp(context, 16);
        hero.addView(connectButton, connectParams);

        hero.addView(directToggle);
        hero.addView(advancedBox);

        View devicesSection = buildDevicesSection(context);
        LinearLayout.LayoutParams sectionParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        sectionParams.topMargin = UiKit.dp(context, 28);
        page.addView(devicesSection, sectionParams);
        return page;
    }

    private View buildHeader(Context context) {
        LinearLayout header = new LinearLayout(context);
        header.setOrientation(LinearLayout.HORIZONTAL);
        header.setGravity(Gravity.CENTER_VERTICAL);

        TextView logo = UiKit.text(context, context.getString(R.string.logo_letter), 17,
                0xFF062015, true);
        logo.setGravity(Gravity.CENTER);
        logo.setBackground(UiKit.rounded(context, UiKit.ACCENT, 11));
        header.addView(logo, new LinearLayout.LayoutParams(UiKit.dp(context, 38),
                UiKit.dp(context, 38)));

        LinearLayout titles = new LinearLayout(context);
        titles.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams titleParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        titleParams.leftMargin = UiKit.dp(context, 11);
        header.addView(titles, titleParams);
        titles.addView(UiKit.text(context, context.getString(R.string.app_name), 19, UiKit.TEXT,
                true));
        TextView subtitle = UiKit.text(context, context.getString(R.string.app_subtitle), 11,
                UiKit.TEXT_DIM, false);
        LinearLayout.LayoutParams subtitleParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        subtitleParams.topMargin = UiKit.dp(context, 3);
        titles.addView(subtitle, subtitleParams);

        View spacer = new View(context);
        header.addView(spacer, new LinearLayout.LayoutParams(0,
                ViewGroup.LayoutParams.WRAP_CONTENT, 1f));

        header.addView(UiKit.pill(context, context.getString(R.string.header_badge), UiKit.ACCENT,
                UiKit.ACCENT_SOFT));
        return header;
    }

    private LinearLayout buildAdvanced(Context context) {
        LinearLayout box = new LinearLayout(context);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setVisibility(View.GONE);
        LinearLayout.LayoutParams boxParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        boxParams.topMargin = UiKit.dp(context, 6);
        box.setLayoutParams(boxParams);

        box.addView(hostField);
        LinearLayout ports = new LinearLayout(context);
        ports.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams portsParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        portsParams.topMargin = UiKit.dp(context, 8);
        box.addView(ports, portsParams);

        LinearLayout.LayoutParams portParams = new LinearLayout.LayoutParams(0,
                UiKit.dp(context, 46), 1f);
        portParams.rightMargin = UiKit.dp(context, 8);
        ports.addView(videoPortField, portParams);
        LinearLayout.LayoutParams secondPort = new LinearLayout.LayoutParams(0,
                UiKit.dp(context, 46), 1f);
        ports.addView(controlPortField, secondPort);

        TextView hint = UiKit.text(context, context.getString(R.string.direct_hint), 10,
                UiKit.TEXT_FAINT, false);
        LinearLayout.LayoutParams hintParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        hintParams.topMargin = UiKit.dp(context, 8);
        box.addView(hint, hintParams);
        return box;
    }

    private View buildDevicesSection(Context context) {
        LinearLayout section = new LinearLayout(context);
        section.setOrientation(LinearLayout.VERTICAL);

        LinearLayout heading = new LinearLayout(context);
        heading.setOrientation(LinearLayout.HORIZONTAL);
        heading.setGravity(Gravity.CENTER_VERTICAL);
        heading.addView(UiKit.text(context, context.getString(R.string.devices_title), 16,
                UiKit.TEXT, true));
        LinearLayout.LayoutParams countParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        countParams.leftMargin = UiKit.dp(context, 8);
        heading.addView(devicesCount, countParams);
        section.addView(heading);

        LinearLayout.LayoutParams listParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        listParams.topMargin = UiKit.dp(context, 12);
        section.addView(devicesList, listParams);

        TextView tip = UiKit.text(context, context.getString(R.string.device_delete_hint), 10,
                UiKit.TEXT_FAINT, false);
        LinearLayout.LayoutParams tipParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        tipParams.topMargin = UiKit.dp(context, 4);
        section.addView(tip, tipParams);
        return section;
    }

    private View buildAboutPage(Context context, String versionText) {
        LinearLayout page = new LinearLayout(context);
        page.setOrientation(LinearLayout.VERTICAL);
        int padding = UiKit.dp(context, 20);
        page.setPadding(padding, UiKit.dp(context, 26), padding, UiKit.dp(context, 24));

        LinearLayout titles = new LinearLayout(context);
        titles.setOrientation(LinearLayout.VERTICAL);
        titles.addView(UiKit.text(context, context.getString(R.string.about_title), 22, UiKit.TEXT,
                true));
        TextView version = UiKit.text(context, versionText, 11, UiKit.TEXT_DIM, false);
        LinearLayout.LayoutParams versionParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        versionParams.topMargin = UiKit.dp(context, 6);
        titles.addView(version, versionParams);
        page.addView(titles);

        page.addView(card(context, R.string.about_feature_title, R.string.about_feature_body));
        page.addView(card(context, R.string.about_gesture_title, R.string.about_gesture_body));

        TextView disclaimer = UiKit.text(context, context.getString(R.string.about_disclaimer), 10,
                UiKit.TEXT_FAINT, false);
        LinearLayout.LayoutParams disclaimerParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        disclaimerParams.topMargin = UiKit.dp(context, 12);
        page.addView(disclaimer, disclaimerParams);
        return page;
    }

    private View card(Context context, int titleRes, int bodyRes) {
        LinearLayout card = new LinearLayout(context);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(UiKit.dp(context, 16), UiKit.dp(context, 16), UiKit.dp(context, 16),
                UiKit.dp(context, 16));
        card.setBackground(UiKit.rounded(context, UiKit.SURFACE, 16));
        card.addView(UiKit.text(context, context.getString(titleRes), 14, UiKit.TEXT, true));
        TextView body = UiKit.text(context, context.getString(bodyRes), 12, UiKit.TEXT_DIM,
                false);
        body.setLineSpacing(UiKit.dp(context, 3), 1f);
        LinearLayout.LayoutParams bodyParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        bodyParams.topMargin = UiKit.dp(context, 8);
        card.addView(body, bodyParams);
        LinearLayout.LayoutParams cardParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        cardParams.topMargin = UiKit.dp(context, 16);
        card.setLayoutParams(cardParams);
        return card;
    }

    private View buildNav(Context context) {
        LinearLayout nav = new LinearLayout(context);
        nav.setOrientation(LinearLayout.VERTICAL);
        nav.setBackgroundColor(0xFF11141A);

        View divider = new View(context);
        divider.setBackgroundColor(UiKit.DIVIDER);
        nav.addView(divider, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, UiKit.dp(context, 1)));

        LinearLayout row = new LinearLayout(context);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setPadding(0, UiKit.dp(context, 8), 0, UiKit.dp(context, 10));
        nav.addView(row, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        int[] icons = { R.drawable.ic_monitor, R.drawable.ic_person };
        int[] labels = { R.string.tab_devices, R.string.tab_about };
        for (int index = 0; index < 2; index++) {
            LinearLayout item = new LinearLayout(context);
            item.setOrientation(LinearLayout.VERTICAL);
            item.setGravity(Gravity.CENTER);
            item.setClickable(true);
            item.setFocusable(true);
            item.setPadding(0, UiKit.dp(context, 4), 0, UiKit.dp(context, 4));

            ImageView icon = new ImageView(context);
            icon.setImageResource(icons[index]);
            icon.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
            item.addView(icon, new LinearLayout.LayoutParams(UiKit.dp(context, 22),
                    UiKit.dp(context, 22)));

            TextView label = UiKit.text(context, context.getString(labels[index]), 10,
                    UiKit.TEXT_FAINT, true);
            LinearLayout.LayoutParams labelParams = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            labelParams.topMargin = UiKit.dp(context, 4);
            item.addView(label, labelParams);

            final int target = index;
            item.setOnClickListener(view -> setTab(target));
            row.addView(item, new LinearLayout.LayoutParams(0,
                    ViewGroup.LayoutParams.WRAP_CONTENT, 1f));

            tabIcons[index] = icon;
            tabLabels[index] = label;
        }
        return nav;
    }

    private FrameLayout.LayoutParams navParams(Context context) {
        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, UiKit.dp(context, 70));
        params.gravity = Gravity.BOTTOM;
        return params;
    }

    private EditText field(Context context, int hintRes, int inputType) {
        EditText edit = new EditText(context);
        edit.setHint(hintRes);
        edit.setInputType(inputType);
        edit.setSingleLine(true);
        edit.setTextSize(13);
        edit.setTextColor(UiKit.TEXT);
        edit.setHintTextColor(UiKit.TEXT_FAINT);
        edit.setBackground(UiKit.rounded(context, UiKit.SURFACE_ALT, 12));
        edit.setPadding(UiKit.dp(context, 12), 0, UiKit.dp(context, 12), 0);
        edit.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO);
        edit.setLayoutParams(new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, UiKit.dp(context, 46)));
        return edit;
    }

    /* ------------------------------------------------------------------ */
    /* Behaviour                                                           */
    /* ------------------------------------------------------------------ */

    private void toggleAdvanced() {
        advancedOpen = !advancedOpen;
        advancedBox.setVisibility(advancedOpen ? View.VISIBLE : View.GONE);
        updateDirectToggle();
        if (advancedOpen) {
            hostField.requestFocus();
            InputMethodManager manager =
                    (InputMethodManager) getContext().getSystemService(Context.INPUT_METHOD_SERVICE);
            if (manager != null) {
                manager.showSoftInput(hostField, InputMethodManager.SHOW_IMPLICIT);
            }
        }
    }

    private void updateDirectToggle() {
        directToggle.setText(advancedOpen ? R.string.direct_link_collapse : R.string.direct_link);
    }

    private void submit() {
        String code = codeField.getCode();
        if (code.length() == 9) {
            DeviceBook.Device device = book.findByCode(code);
            if (device != null) {
                hideKeyboard();
                listener.onConnect(device);
                return;
            }
        }

        String host = hostField.getText().toString().trim();
        if (advancedOpen && !host.isEmpty()) {
            int videoPort = parsePort(videoPortField);
            int controlPort = parsePort(controlPortField);
            if (videoPort <= 0 || controlPort <= 0 || videoPort == controlPort) {
                showNotice(getContext().getString(R.string.port_invalid));
                return;
            }
            hideKeyboard();
            listener.onConnect(new DeviceBook.Device("", host, host, videoPort, controlPort,
                    System.currentTimeMillis()));
            return;
        }

        if (code.length() > 0) {
            showNotice(getContext().getString(R.string.code_unknown));
        } else if (advancedOpen) {
            showNotice(getContext().getString(R.string.host_invalid));
        } else {
            showNotice(getContext().getString(R.string.code_required));
        }
    }

    private int parsePort(EditText edit) {
        try {
            int port = Integer.parseInt(edit.getText().toString().trim());
            return port >= 1 && port <= 65535 ? port : -1;
        } catch (NumberFormatException error) {
            return -1;
        }
    }

    DeviceBook.Device remember(DeviceBook.Device device) {
        DeviceBook.Device stored = device.code.isEmpty()
                ? book.save(device.name, device.host, device.videoPort, device.controlPort, null)
                : book.touch(device);
        refreshDevices();
        return stored;
    }

    void refreshDevices() {
        Context context = getContext();
        devicesList.removeAllViews();
        List<DeviceBook.Device> devices = book.all();
        devicesCount.setText(context.getString(R.string.devices_count, devices.size()));

        if (devices.isEmpty()) {
            LinearLayout empty = new LinearLayout(context);
            empty.setOrientation(LinearLayout.VERTICAL);
            empty.setGravity(Gravity.CENTER);
            empty.setPadding(UiKit.dp(context, 20), UiKit.dp(context, 24), UiKit.dp(context, 20),
                    UiKit.dp(context, 24));
            empty.setBackground(UiKit.rounded(context, 0xFF14181F, 16));
            empty.addView(UiKit.text(context, context.getString(R.string.devices_empty_title), 13,
                    UiKit.TEXT, true));
            TextView body = UiKit.text(context, context.getString(R.string.devices_empty_body), 11,
                    UiKit.TEXT_DIM, false);
            body.setGravity(Gravity.CENTER);
            LinearLayout.LayoutParams bodyParams = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            bodyParams.topMargin = UiKit.dp(context, 6);
            empty.addView(body, bodyParams);
            devicesList.addView(empty);
            return;
        }

        for (DeviceBook.Device device : devices) {
            devicesList.addView(deviceCard(context, device));
        }
    }

    private View deviceCard(Context context, DeviceBook.Device device) {
        LinearLayout card = new LinearLayout(context);
        card.setOrientation(LinearLayout.HORIZONTAL);
        card.setGravity(Gravity.CENTER_VERTICAL);
        card.setPadding(UiKit.dp(context, 14), UiKit.dp(context, 13), UiKit.dp(context, 12),
                UiKit.dp(context, 13));
        card.setBackground(UiKit.ripple(context, UiKit.rounded(context, UiKit.SURFACE, 16)));
        card.setClickable(true);
        card.setFocusable(true);
        card.setOnClickListener(view -> listener.onConnect(device));
        card.setOnLongClickListener(view -> {
            confirmDelete(device);
            return true;
        });

        ImageView icon = new ImageView(context);
        icon.setImageResource(R.drawable.ic_monitor);
        icon.setColorFilter(UiKit.ACCENT);
        icon.setPadding(UiKit.dp(context, 9), UiKit.dp(context, 9), UiKit.dp(context, 9),
                UiKit.dp(context, 9));
        icon.setBackground(UiKit.rounded(context, UiKit.SURFACE_ALT, 12));
        icon.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        card.addView(icon, new LinearLayout.LayoutParams(UiKit.dp(context, 42),
                UiKit.dp(context, 42)));

        LinearLayout info = new LinearLayout(context);
        info.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams infoParams = new LinearLayout.LayoutParams(0,
                ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        infoParams.leftMargin = UiKit.dp(context, 12);
        card.addView(info, infoParams);

        info.addView(UiKit.text(context, device.name, 14, UiKit.TEXT, true));
        TextView address = UiKit.text(context, device.addressText(), 11, UiKit.TEXT_DIM, false);
        LinearLayout.LayoutParams addressParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        addressParams.topMargin = UiKit.dp(context, 3);
        info.addView(address, addressParams);

        TextView code = UiKit.text(context,
                context.getString(R.string.device_code, spaced(device.code)), 10, UiKit.ACCENT, false);
        code.setTypeface(Typeface.create("monospace", Typeface.NORMAL));
        LinearLayout.LayoutParams codeParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        codeParams.topMargin = UiKit.dp(context, 3);
        info.addView(code, codeParams);

        TextView last = UiKit.text(context,
                context.getString(R.string.device_last_used, relativeTime(device.lastUsedMs)), 10,
                UiKit.TEXT_FAINT, false);
        LinearLayout.LayoutParams lastParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lastParams.topMargin = UiKit.dp(context, 2);
        info.addView(last, lastParams);

        TextView action = UiKit.pill(context, context.getString(R.string.device_connect),
                UiKit.ACCENT, UiKit.ACCENT_SOFT);
        action.setPadding(UiKit.dp(context, 12), UiKit.dp(context, 7), UiKit.dp(context, 12),
                UiKit.dp(context, 7));
        card.addView(action);

        LinearLayout.LayoutParams cardParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        cardParams.bottomMargin = UiKit.dp(context, 10);
        card.setLayoutParams(cardParams);
        return card;
    }

    private void confirmDelete(DeviceBook.Device device) {
        new AlertDialog.Builder(getContext())
                .setTitle(R.string.device_delete_title)
                .setMessage(device.name)
                .setNegativeButton(R.string.cancel, null)
                .setPositiveButton(R.string.delete, (dialog, which) -> listener.onDelete(device))
                .show();
    }

    void showNotice(String text) {
        handler.removeCallbacks(hideNotice);
        noticeView.setText(text);
        noticeView.setVisibility(View.VISIBLE);
        handler.postDelayed(hideNotice, 4000);
    }

    void clearCode() {
        codeField.clear();
    }

    void removeDevice(DeviceBook.Device device) {
        book.remove(device);
        refreshDevices();
        showNotice(getContext().getString(R.string.device_deleted));
    }

    void showDevicesTab() {
        setTab(TAB_DEVICES);
    }

    void focusCode() {
        codeField.focusInput();
    }

    private void hideKeyboard() {
        InputMethodManager manager = (InputMethodManager) getContext().getSystemService(
                Context.INPUT_METHOD_SERVICE);
        if (manager != null) {
            manager.hideSoftInputFromWindow(getWindowToken(), 0);
        }
    }

    private void setTab(int tab) {
        currentTab = tab;
        devicesPage.setVisibility(tab == TAB_DEVICES ? View.VISIBLE : View.GONE);
        aboutPage.setVisibility(tab == TAB_ABOUT ? View.VISIBLE : View.GONE);
        for (int index = 0; index < tabIcons.length; index++) {
            boolean active = index == tab;
            tabIcons[index].setColorFilter(active ? UiKit.ACCENT : UiKit.TEXT_FAINT);
            tabLabels[index].setTextColor(active ? UiKit.ACCENT : UiKit.TEXT_FAINT);
        }
    }

    int currentTab() {
        return currentTab;
    }

    private static String spaced(String code) {
        if (code.length() != 9) {
            return code;
        }
        return code.substring(0, 3) + " " + code.substring(3, 6) + " " + code.substring(6);
    }

    private static String relativeTime(long millis) {
        if (millis <= 0) {
            return "—";
        }
        return new SimpleDateFormat("MM-dd HH:mm", Locale.getDefault()).format(new Date(millis));
    }
}
