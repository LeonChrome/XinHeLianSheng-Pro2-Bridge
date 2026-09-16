package apierror

import "github.com/Alia5/VIIPER/viipertypes"

func ErrBadRequest(detail string) viipertypes.APIError {
	return viipertypes.APIError{Status: 400, Title: "Bad Request", Detail: detail}
}
func ErrNotFound(detail string) viipertypes.APIError {
	return viipertypes.APIError{Status: 404, Title: "Not Found", Detail: detail}
}
func ErrConflict(detail string) viipertypes.APIError {
	return viipertypes.APIError{Status: 409, Title: "Conflict", Detail: detail}
}
func ErrInternal(detail string) viipertypes.APIError {
	return viipertypes.APIError{Status: 500, Title: "Internal Server Error", Detail: detail}
}
func ErrUnauthorized(detail string) viipertypes.APIError {
	return viipertypes.APIError{Status: 401, Title: "Unauthorized", Detail: detail}
}

// WrapError normalizes any error into viipertypes.ApiError.
func WrapError(err error) viipertypes.APIError {
	if ae, ok := err.(*viipertypes.APIError); ok {
		return *ae
	}
	if ae, ok := err.(viipertypes.APIError); ok {
		return ae
	}
	// Default wrap as internal error
	return ErrInternal(err.Error())
}
