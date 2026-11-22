package org.screencast.app;

import android.annotation.SuppressLint;
import android.content.Context;
import android.view.MotionEvent;
import android.view.SurfaceView;

/**
 * The video surface. Its size is driven by {@code MainActivity} so that it always matches the
 * decoded picture's aspect ratio; the H.264 decoder renders directly into this view's
 * {@link android.view.Surface}, so no decoded frame is ever copied through Java.
 *
 * <p>Touch handling is delegated to {@link TouchRouter}. {@link #performClick()} is called on
 * every gesture end so the view stays usable for accessibility services even though the real
 * interaction model is a trackpad.</p>
 *
 * <p>This view is only ever created in code (it needs the router), which is why it has no
 * {@code (Context, AttributeSet)} constructor.</p>
 */
@SuppressLint("ViewConstructor")
final class VideoSurfaceView extends SurfaceView {

    private final TouchRouter router;

    VideoSurfaceView(Context context, TouchRouter router) {
        super(context);
        this.router = router;

        setFocusable(true);
        setFocusableInTouchMode(true);
        setKeepScreenOn(true);
    }

    @Override
    public boolean onTouchEvent(MotionEvent event) {
        if (event.getActionMasked() == MotionEvent.ACTION_DOWN) {
            requestFocus();
        }

        boolean handled = router.onTouchEvent(event, getWidth(), getHeight());
        if (event.getActionMasked() == MotionEvent.ACTION_UP) {
            performClick();
        }
        return handled;
    }

    @Override
    public boolean performClick() {
        return super.performClick();
    }
}
