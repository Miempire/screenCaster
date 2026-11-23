package org.screencast.app;

import android.annotation.SuppressLint;
import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.text.Editable;
import android.text.InputFilter;
import android.text.InputType;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.View;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

/**
 * The nine-box connection code field.
 *
 * <p>A single invisible {@link EditText} owns the text and the soft keyboard; the nine boxes only
 * paint its content. Delegating to a real editor keeps paste, backspace and IME behaviour working
 * instead of reimplementing key handling on a custom view.</p>
 */
@SuppressLint("ViewConstructor")
final class CodeFieldView extends FrameLayout {

    private static final int LENGTH = 9;

    private final TextView[] slots = new TextView[LENGTH];
    private final EditText input;
    private final LinearLayout row;

    CodeFieldView(Context context) {
        super(context);

        row = new LinearLayout(context);
        row.setOrientation(LinearLayout.HORIZONTAL);
        for (int index = 0; index < LENGTH; index++) {
            TextView slot = UiKit.text(context, "", 22, UiKit.TEXT, true);
            slot.setTypeface(Typeface.create("monospace", Typeface.BOLD));
            slot.setGravity(Gravity.CENTER);
            slot.setBackground(UiKit.rounded(context, UiKit.SURFACE_ALT, 12));
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0,
                    UiKit.dp(context, 50), 1f);
            int gap = UiKit.dp(context, 6);
            params.setMargins(index == 0 ? 0 : gap / 2, 0, index == LENGTH - 1 ? 0 : gap / 2, 0);
            row.addView(slot, params);
            slots[index] = slot;
        }
        addView(row, new FrameLayout.LayoutParams(LayoutParams.MATCH_PARENT,
                LayoutParams.WRAP_CONTENT));

        input = new EditText(context);
        input.setInputType(InputType.TYPE_CLASS_NUMBER);
        input.setFilters(new InputFilter[] { new InputFilter.LengthFilter(LENGTH), new DigitsOnly() });
        input.setImeOptions(EditorInfo.IME_ACTION_DONE);
        input.setSingleLine(true);
        input.setCursorVisible(false);
        input.setTextColor(Color.TRANSPARENT);
        input.setBackground(null);
        input.setAlpha(0.02f);
        input.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO);
        input.setTextSize(1);
        addView(input, new FrameLayout.LayoutParams(UiKit.dp(context, 2), UiKit.dp(context, 2)));

        input.addTextChangedListener(new TextWatcher() {
            @Override
            public void beforeTextChanged(CharSequence text, int start, int count, int after) {
                // The boxes only render the committed value.
            }

            @Override
            public void onTextChanged(CharSequence text, int start, int before, int count) {
                // The boxes only render the committed value.
            }

            @Override
            public void afterTextChanged(Editable editable) {
                render(editable.length());
            }
        });

        setOnClickListener(view -> focusInput());
        for (TextView slot : slots) {
            slot.setOnClickListener(view -> focusInput());
        }
        input.setOnFocusChangeListener((view, focused) -> render(input.length()));

        setContentDescription(context.getString(R.string.code_hint));
        render(0);
    }

    String getCode() {
        return input.getText().toString();
    }

    void clear() {
        input.setText("");
    }

    void setCode(String code) {
        input.setText(code == null ? "" : code);
    }

    void focusInput() {
        input.requestFocus();
        InputMethodManager manager =
                (InputMethodManager) getContext().getSystemService(Context.INPUT_METHOD_SERVICE);
        if (manager != null) {
            manager.showSoftInput(input, InputMethodManager.SHOW_IMPLICIT);
        }
    }

    private void render(int count) {
        Context context = getContext();
        for (int index = 0; index < LENGTH; index++) {
            String text = input.getText().toString();
            slots[index].setText(index < text.length() ? text.substring(index, index + 1) : "");
            boolean active = input.hasFocus() && index == Math.min(count, LENGTH - 1);
            slots[index].setBackground(active
                    ? UiKit.stroked(context, UiKit.SURFACE_ALT, UiKit.ACCENT, 12, 1.5f)
                    : UiKit.rounded(context, UiKit.SURFACE_ALT, 12));
        }
    }

    /** Drops spaces and dashes from pasted text; the wire format is plain digits. */
    private static final class DigitsOnly implements InputFilter {
        @Override
        public CharSequence filter(CharSequence source, int start, int end, android.text.Spanned dest,
                int dstart, int dend) {
            StringBuilder kept = new StringBuilder(end - start);
            for (int index = start; index < end; index++) {
                char character = source.charAt(index);
                if (character >= '0' && character <= '9') {
                    kept.append(character);
                }
            }
            return kept.length() == end - start ? null : kept.toString();
        }
    }
}
